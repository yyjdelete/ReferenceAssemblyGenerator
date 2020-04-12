using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommandLine;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace ReferenceAssemblyGenerator.CLI
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var result = Parser.Default.ParseArguments<ProgramOptions>(args)
                .WithParsed(RunWithOptions);

            return result.Tag == ParserResultType.Parsed ? 0 : 1;
        }

        private static ProgramOptions s_ProgamOptions;

        private static void RunWithOptions(ProgramOptions opts)
        {
            s_ProgamOptions = opts;

            if (!File.Exists(opts.AssemblyPath))
            {
                throw new FileNotFoundException("Assembly file was not found", opts.AssemblyPath);
            }

            if (string.IsNullOrEmpty(opts.OutputFile))
            {
                string fileName = Path.GetFileNameWithoutExtension(opts.AssemblyPath);
                string extension = Path.GetExtension(opts.AssemblyPath);

                opts.OutputFile = opts.AssemblyPath.Replace(fileName + extension, fileName + "-reference" + extension);
            }

            if (File.Exists(opts.OutputFile) && !opts.Force)
            {
                throw new Exception("Output file exists already. Use --force to override it.");
            }

            byte[] assemblyData = File.ReadAllBytes(opts.AssemblyPath);

            using (MemoryStream inputStream = new MemoryStream(assemblyData))
            {
                ModuleDefMD module = ModuleDefMD.Load(inputStream);
                if (s_ProgamOptions.KeepInternal > 1)
                {
                    s_ProgamOptions.KeepInternal = (byte)(module.Assembly.CustomAttributes.IsDefined("System.Runtime.CompilerServices.InternalsVisibleToAttribute") ? 1 : 0);
                }
                module.IsILOnly = true;
                module.VTableFixups = null;
                if (module.IsStrongNameSigned && !s_ProgamOptions.DelaySign)
                {
                    module.IsStrongNameSigned = false;
                    module.Assembly.PublicKey = null;
                    module.Assembly.HasPublicKey = false;
                }

                CheckTypes(module.Types);

                CheckCustomAttributes(module.Assembly.CustomAttributes);
                CheckCustomAttributes(module.CustomAttributes);
                if (s_ProgamOptions.InjectReferenceAssemblyAttribute)
                    InjectReferenceAssemblyAttribute(module);

                if (File.Exists(opts.OutputFile))
                {
                    File.Delete(opts.OutputFile);
                }

                using (MemoryStream outputStream = new MemoryStream())
                {
                    var moduleOpts = new dnlib.DotNet.Writer.ModuleWriterOptions(module)
                    {
                        ShareMethodBodies = true,
                        AddMvidSection = true,
                        DelaySign = s_ProgamOptions.DelaySign,
                        Logger = ErrorLogger.Instance,//sender is dnlib.DotNet.Writer.ModuleWriter
                        //MetadataLogger = ErrorLogger.Instance,//dnlib.DotNet.Writer.Metadata
                    };
                    module.Write(outputStream, moduleOpts);
                    outputStream.Position = 0;
                    using (var fileStream = File.Create(opts.OutputFile))
                    {
                        outputStream.CopyTo(fileStream);
                    }
                }
            }
        }

        private static void InjectReferenceAssemblyAttribute(ModuleDefMD module)
        {
            if (module.Assembly.CustomAttributes.IsDefined("System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
                return;
            try
            {
                //Don't check types in corlib, since reference assemblies not always available and match
                var attr = module.Find("System.Runtime.CompilerServices.ReferenceAssemblyAttribute", false);
                if (attr == null)
                {
                    attr = new TypeDefUser("System.Runtime.CompilerServices", "ReferenceAssemblyAttribute",
                        new TypeRefUser(module, "System", "Attribute", module.CorLibTypes.AssemblyRef))
                    {
                        Visibility = TypeAttributes.NotPublic,
                        IsSealed = true
                    };
                    var implFlags = MethodImplAttributes.IL | MethodImplAttributes.Managed;
                    var flags = MethodAttributes.Public |
                                MethodAttributes.HideBySig |
                                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                    var ctor = new MethodDefUser(".ctor",
                        MethodSig.CreateInstance(module.CorLibTypes.Void), implFlags, flags);
                    attr.Methods.Add(ctor);
                    module.Types.Add(attr);

                    ctor.Body = new CilBody();
                    PurgeMethodBody(ctor);
                }
                module.Assembly.CustomAttributes.Add(new CustomAttribute(attr.FindDefaultConstructor()));
            }
            catch { }
        }

        private static void CheckTypes(IList<TypeDef> types)
        {
            if (types.Count == 0)
                return;

            foreach (var type in types.ToArray())
            {
                CheckType(type, types);
            }
        }

        private static void CheckType(TypeDef type, IList<TypeDef> parent)
        {
            if (!IsReachable(type, false))
            {
                if (type.IsGlobalModuleType)
                {
                    //DO NOT remove this type
                    type.CustomAttributes.Clear();
                    type.GenericParameters.Clear();
                    type.Interfaces.Clear();
                    type.Methods.Clear();
                    type.Fields.Clear();
                    type.Properties.Clear();
                    type.Events.Clear();
                    type.NestedTypes.Clear();
                    return;
                }
                parent.Remove(type);
                return;
            }

            CheckCustomAttributes(type.CustomAttributes);
            CheckGenericParams(type.GenericParameters);

            if (type.Interfaces.Count > 0)
            {
                foreach (var @interface in type.Interfaces.ToArray())
                {
                    if (!IsReachable(@interface.Interface))
                    {
                        type.Interfaces.Remove(@interface);
                        //Remove invisible interfaces first, to simply check for methods overrides
                        foreach (var method in type.Methods)
                        {
                            if (!method.HasOverrides)
                                continue;
                            foreach (var mo in method.Overrides.ToArray())
                            {
                                if (mo.MethodDeclaration.DeclaringType == @interface.Interface)
                                {
                                    method.Overrides.Remove(mo);
                                }
                            }
                        }
                        continue;
                    }
                    CheckCustomAttributes(@interface.CustomAttributes);
                }
            }

            if (type.Methods.Count > 0)
            {
                foreach (var method in type.Methods.ToArray())
                {
                    if (!IsReachable(method))
                    {
                        type.Methods.Remove(method);
                        continue;
                    }
                    CheckCustomAttributes(method.CustomAttributes);
                    //NOTE: Parameters begins with `this`(without ParamDef)
                    foreach (var mp in method.Parameters)
                    {
                        if (mp.ParamDef != null)
                            CheckCustomAttributes(mp.ParamDef.CustomAttributes);
                        Debug.Assert(IsReachable(mp.Type));
                    }
                    if (method.Parameters.ReturnParameter.ParamDef != null)
                        CheckCustomAttributes(method.Parameters.ReturnParameter.ParamDef.CustomAttributes);
                    //It's strange but `UnityScript.Lang` reference an internal type as ReturnParameter in an public method of public class.
                    Debug.Assert(IsReachable(method.Parameters.ReturnParameter.Type));

                    CheckGenericParams(method.GenericParameters);

                    PurgeMethodBody(method);
                }

                if (!type.IsValueType && !(type.IsSealed && type.IsAbstract) && !type.IsInterface && !type.FindInstanceConstructors().Any())
                {
                    //If it's an non-static class, inject an private default `.ctor` after all `.ctor` is removed.
                    var implFlags = MethodImplAttributes.IL | MethodImplAttributes.Managed;
                    var flags = MethodAttributes.Private |
                                MethodAttributes.HideBySig |
                                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                    var ctor = new MethodDefUser(".ctor",
                        MethodSig.CreateInstance(type.Module.CorLibTypes.Void), implFlags, flags);
                    type.Methods.Insert(0, ctor);

                    ctor.Body = new CilBody();
                    PurgeMethodBody(ctor);
                }
            }

            if (type.Fields.Count > 0)
            {
                byte mode = 0;
                if (!s_ProgamOptions.KeepNonPublic && type.IsValueType)
                {
                    foreach (var field in type.Fields)
                    {
                        if (field.IsStatic)
                            continue;
                        //This should be done if any thing will be removed, it may remove an object or T and keep an int.
                        if (field.Access == FieldAttributes.Public || s_ProgamOptions.KeepInternal != 0 && field.Access == FieldAttributes.Family)
                        {
                            //mode = 0;
                            //break;
                            continue;
                        }
                        //Don't do deep and strict look, just simply check IsPrimitive, maybe broken `unmanaged`
                        if (field.FieldType.IsPrimitive)
                            mode |= 1;
                        else
                            mode |= 2;
                        if (mode == 3)
                            break;
                    }
                }
                foreach (var field in type.Fields.ToArray())
                {
                    if (!IsReachable(field))
                    {
                        type.Fields.Remove(field);
                        continue;
                    }
                    CheckCustomAttributes(field.CustomAttributes);
                    Debug.Assert(IsReachable(field.FieldType));
                }
                if (mode != 0)
                {
                    //special handle for struct
                    //Add `private int _dummyPrimitive;` to make roslyn know it's not an empty struct and need be init;
                    var fieldAttr = FieldAttributes.Private;
                    bool isReadonly = type.CustomAttributes.IsDefined("System.Runtime.CompilerServices.IsReadOnlyAttribute");
                    //Match rule for `readonly struct`: use readonly for readonly struct
                    if (isReadonly)
                        fieldAttr |= FieldAttributes.InitOnly;
                    //Add `private object _dummy;` to match rule for `unmanaged`: The type must be a non-nullable value type, along with all fields at any level of nesting, in order to use it as parameter 'T' in the generic type or method
                    //NOTE: runtime reference assembly can have both field(_dummy first) and `private T[] _array` `private readonly TResult _result;`
                    var fieldType = type.Module.CorLibTypes.Int32;
                    var fieldName = "_dummyPrimitive";
                    if (mode == 0)
                        throw new InvalidOperationException("Unreachable code. ");
                    if ((mode & 2) != 0)
                    {
                        fieldName = "_dummy";
                        fieldType = type.Module.CorLibTypes.Object;
                    }
                    type.Fields.Insert(0, new FieldDefUser(fieldName, new FieldSig(fieldType), fieldAttr));
                }
            }

            if (type.Properties.Count > 0)
            {
                foreach (var property in type.Properties.ToArray())
                {
                    //methods already checked in type.Methods
                    if (property.IsEmpty)
                    {
                        type.Properties.Remove(property);
                        continue;
                    }
                    CheckCustomAttributes(property.CustomAttributes);
                }
            }

            if (type.Events.Count > 0)
            {
                foreach (var @event in type.Events.ToArray())
                {
                    //methods already checked in type.Methods
                    if (@event.IsEmpty)
                    {
                        type.Events.Remove(@event);
                        continue;
                    }
                    CheckCustomAttributes(@event.CustomAttributes);
                    Debug.Assert(IsReachable(@event.EventType));
                }
            }

            CheckTypes(type.NestedTypes);
        }

        private static void CheckGenericParams(IList<GenericParam> genericParameters)
        {
            if (genericParameters.Count == 0)
                return;
            foreach (var genericPar in genericParameters)
            {
                CheckCustomAttributes(genericPar.CustomAttributes);
                CheckGenericParamConstraints(genericPar.GenericParamConstraints);
            }
        }

        private static void CheckGenericParamConstraints(IList<GenericParamConstraint> genericParamConstraints)
        {
            if (genericParamConstraints.Count == 0)
                return;
            foreach (var genericPar in genericParamConstraints.ToArray())
            {
                if (!IsReachable(genericPar.Constraint))
                {
                    //The code path should be unreachable
                    genericParamConstraints.Remove(genericPar);
                    continue;
                }
                CheckCustomAttributes(genericPar.CustomAttributes);
            }
        }

        private static bool IsWellKnownCompilerInjectedType(TypeDef t)
        {
            return (t.Visibility == TypeAttributes.NotPublic &&
                (t.FullName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) ||
                t.FullName.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal) ||
                t.FullName.StartsWith("System.Diagnostics.CodeAnalysis", StringComparison.Ordinal)));
        }

        private static bool IsReachable(TypeDef t, bool doNestedCheck)
        {
            if (s_ProgamOptions.KeepNonPublic)
                return true;
            if (t == null)
                return false;
            //if (t.IsGlobalModuleType)
            //    return true;
            //Skip compile inject internal attributes
            if (IsWellKnownCompilerInjectedType(t))
                return true;
            //IsReachable for DeclaringType was checked in outter loop.
            switch (t.Visibility)
            {
                case TypeAttributes.Public://public
                    return true;
                case TypeAttributes.NestedPublic://public
                    return true;// && IsReachable(t.DeclaringType);
                case TypeAttributes.NestedFamily://protected
                case TypeAttributes.NestedFamORAssem://protected internal
                    return true && (!doNestedCheck || IsReachable(t.DeclaringType, doNestedCheck));
                case TypeAttributes.NotPublic://internal
                    return s_ProgamOptions.KeepInternal != 0;
                case TypeAttributes.NestedAssembly://internal
                case TypeAttributes.NestedFamANDAssem://private protected
                    return s_ProgamOptions.KeepInternal != 0 && (!doNestedCheck || IsReachable(t.DeclaringType, doNestedCheck));
                case TypeAttributes.NestedPrivate://private
                    return false;
                default:
                    throw new InvalidOperationException($"Unreachable code. <-IsReachable(TypeDef.Visibility: {t.Visibility})");
            }
        }

        private static bool IsReachable(TypeSpec t)
        {
            return IsReachable(t?.TypeSig);
        }

        private static bool IsReachable(TypeSig t)
        {
            if (t == null)
                return false;
            t = t.RemovePinnedAndModifiers();
            if (t is ByRefSig || t is PtrSig || t is ArraySigBase)
            {
                return IsReachable(t.Next);
            }
            if (t.ScopeType != null && !IsReachable(t.ScopeType))
            {
                return false;
            }
            if (t.IsGenericParameter)
                return true;
            if (t is TypeDefOrRefSig)
            {
                return IsReachable(((TypeDefOrRefSig)t).TypeDefOrRef);
            }
            if (t is GenericInstSig)
            {
                return ((GenericInstSig)t).GenericArguments.All(ga => IsReachable(ga));
            }
            throw new InvalidOperationException($"Unreachable code. <-IsReachable(TypeSig: {t.GetType().FullName}, TypeSig.ElementType: {t.ElementType})");
        }

        private static bool IsReachable(TypeRef t)
        {
            //Don't want to look into references, since reference assemblies not always available and match
            return t != null;
        }

        private static bool IsReachable(ITypeDefOrRef t)
        {
            if (t == null)
                return false;
            if (t.IsTypeDef)
                return IsReachable((TypeDef)t, true);
            if (t.IsTypeRef)
                return IsReachable((TypeRef)t);
            if (t.IsTypeSpec)
                return IsReachable((TypeSpec)t);
            throw new InvalidOperationException($"Unreachable code. <-IsReachable(ITypeDefOrRef: {t.GetType().FullName})");
        }

        private static bool IsReachable(FieldDef f)
        {
            if (s_ProgamOptions.KeepNonPublic)
                return true;
            //IsReachable for DeclaringType was checked in outter loop.
            switch (f.Access)
            {
                case FieldAttributes.Public://public
                    return true;
                case FieldAttributes.Family://protected
                case FieldAttributes.FamORAssem://protected internal
                    return true;
                case FieldAttributes.FamANDAssem://private protected
                case FieldAttributes.Assembly://internal
                    return s_ProgamOptions.KeepInternal != 0;
                case FieldAttributes.PrivateScope://??
                case FieldAttributes.Private://private
                    return false;
                default:
                    throw new InvalidOperationException($"Unreachable code. <-IsReachable(FieldDef.Access: {f.Access})");
            }
        }

        private static bool IsReachable(MethodDef m)
        {
            if (s_ProgamOptions.KeepNonPublic)
                return true;
            if (m.IsStaticConstructor)
                return false;
            bool isReachable;
            //IsReachable for DeclaringType was checked in outter loop.
            switch (m.Access)
            {
                case MethodAttributes.Public://public
                    return true;
                case MethodAttributes.Family://protected
                case MethodAttributes.FamORAssem://protected internal
                    return true;
                case MethodAttributes.FamANDAssem://private protected
                case MethodAttributes.Assembly://internal
                    isReachable = s_ProgamOptions.KeepInternal != 0;
                    break;
                case MethodAttributes.PrivateScope://??
                    isReachable = false;
                    break;
                case MethodAttributes.Private://private
                    {
                        //IsReachable for DeclaringType of overrides was checked and removed in outter loop.
                        //keeps `explicit interface member implementations`
                        isReachable = m.HasOverrides;
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unreachable code. <-IsReachable(MethodDef.Access: {m.Access})");
            }
            //Keep `.ctor` it's the default one or the only one left and doesn't have an unreachable param.
            if (!isReachable && m.IsInstanceConstructor && !m.DeclaringType.IsValueType)
            {
                var ctor0 = m.DeclaringType.FindDefaultConstructor();
                if (m == ctor0)
                    return true;
                if (ctor0 == null && m.MethodSig.Params.All(pd => IsReachable(pd)) &&
                    !m.DeclaringType.FindInstanceConstructors().Any(c => c.Access >= ((s_ProgamOptions.KeepInternal != 0) ? MethodAttributes.FamANDAssem : MethodAttributes.Family)))
                    return true;
            }
            return isReachable;
        }

        private static void CheckCustomAttributes(CustomAttributeCollection collection)
        {
            if (collection.Count == 0)
                return;
            foreach (var attr in collection.ToArray())
            {
                if (IsReachable(attr.AttributeType) && (!attr.Constructor.IsMethodDef || IsReachable((MethodDef)attr.Constructor)))
                    continue;
                collection.Remove(attr);
            }
        }
        public class A
        {
            public A(out int _) { new A(out _); }
        }

        private static void PurgeMethodBody(MethodDef method)
        {
            IMethod baseTypeCtorRef = null;
            PurgeMethodBody(method, ref baseTypeCtorRef);
        }

        private static void PurgeMethodBody(MethodDef method, ref IMethod baseTypeCtorRef)
        {
            if (method.IsAbstract)
            {
                return;
            }
            if (method.DeclaringType.IsDelegate)
            {
                //Ignore extern methods in delegate
                return;
            }
            if (!s_ProgamOptions.UseRuntimeMode && (!method.IsIL || method.Body == null))
            {
                Console.WriteLine($"Skipped method: {method.FullName} (NO IL BODY)");
                return;
            }
            method.CodeType = MethodImplAttributes.IL;
            method.ImplAttributes &= (MethodImplAttributes)0x1f8;
            method.ImplMap = null;

            var oldBody = method.Body;
            method.Body = new CilBody();
            bool isSimple;
            if (s_ProgamOptions.UseRet)
            {
                isSimple = true;
            }
            else
            {
                isSimple = s_ProgamOptions.UseRuntimeMode;
                if (isSimple)
                    isSimple = !method.HasReturnType && !method.ParamDefs.Any(p => p.IsOut && !p.IsIn);
                if (isSimple && method.IsInstanceConstructor)
                {
                    if (method.DeclaringType.IsValueType)
                    {
                        //For struct, always throw null?
                        isSimple = false;
                    }
                    else if (method.DeclaringType.BaseType == null)
                    {
                        //Special case for System.Object
                    }
                    else
                    {
                        //Don't want to look into references, since reference assemblies not always available and match
                        if (baseTypeCtorRef == null && method.DeclaringType.BaseType.ToTypeSig() == method.Module.CorLibTypes.Object)
                        {
                            baseTypeCtorRef = new MemberRefUser(method.Module, ".ctor", MethodSig.CreateInstance(method.Module.CorLibTypes.Void), method.DeclaringType.BaseType);
                        }
                        if (baseTypeCtorRef == null && oldBody != null && oldBody.Instructions.Count >= 3)
                        {
                            // <field init>, ldarg_0, call, <ctor>, retn
                            for (int i = 0; i < oldBody.Instructions.Count - 2; ++i)
                            {
                                var inst0 = oldBody.Instructions[i];
                                var inst1 = oldBody.Instructions[i + 1];
                                if (inst0.IsLdarg() && inst0.GetParameterIndex() == 0 &&
                                    inst1.OpCode == OpCodes.Call && inst1.Operand is IMethod ctorMethod &&
                                    ctorMethod.DeclaringType == method.DeclaringType.BaseType && ctorMethod.Name == ".ctor" &&
                                    ctorMethod.MethodSig.Params.Count == 0 && ctorMethod.MethodSig.GenParamCount == 0)
                                {
                                    baseTypeCtorRef = ctorMethod;
                                    break;
                                }
                            }
                        }
                        if (baseTypeCtorRef == null)
                        {
                            //TODO:TypeSpec for generic types
                            var baseTypeCtor = (method.DeclaringType.BaseType as TypeDef)?.FindDefaultConstructor();
                            if (baseTypeCtor != null && (baseTypeCtor.Access >= MethodAttributes.Family || (baseTypeCtor.Access >= MethodAttributes.Family && baseTypeCtor.DeclaringType.Module == method.DeclaringType.Module)))
                            {
                                baseTypeCtorRef = baseTypeCtor;
                            }
                        }
                        if (baseTypeCtorRef != null)
                        {
                            //For class, always call `base.ctor()`(if exists and visible) without check count of self params. See `System.NullReferenceException`
                            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseTypeCtorRef));
                        }
                        else
                        {
                            //if not exists, see `System.Collections.ObjectModel.ReadOnlyObservableCollection<T>` and `System.Data.SqlClient.SqlRowUpdatedEventArgs`(different mode?) and `System.Threading.Tasks.Task<T>`
                            isSimple = false;
                        }
                    }
                }

                if (!isSimple)
                {
                    // This is what Roslyn does with /refout and /refonly
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
                }
            }
            if (isSimple)
            {
                var retn = Instruction.Create(OpCodes.Ret);

                //Do special handle for `protected override Finalize()//~Object()` as runtime refs does
                //Not really checks override since roslyn also only checks protected void(but not override) for base type.
                if (s_ProgamOptions.UseRuntimeMode && !method.IsStatic && method.Access == MethodAttributes.Family && method.Name == "Finalize"
                    && method.ParamDefs.Count == 0 && !method.HasGenericParameters && method.DeclaringType.BaseType != null && method.Overrides.Count == 1)
                {
                    //See `System.Runtime.InteropServices.SafeHandle`
                    /*try{}finally{base.Finalize()}*/
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Leave_S, retn));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    IMethod baseMethod = method.Overrides[0].MethodDeclaration;
                    bool doAnotherCheck = method.DeclaringType.BaseType != baseMethod.DeclaringType;
                    if (doAnotherCheck && oldBody != null && oldBody.Instructions.Count >= 5 && oldBody.Instructions[oldBody.Instructions.Count - 3].OpCode == OpCodes.Call && oldBody.Instructions[oldBody.Instructions.Count - 3].Operand is IMethod)
                    {
                        var callMethod = (IMethod)oldBody.Instructions[oldBody.Instructions.Count - 3].Operand;
                        if (callMethod.Name == "Finalize" && callMethod.MethodSig.RetType == method.Module.CorLibTypes.Void && callMethod.MethodSig.Params.Count == 0)
                        {
                            baseMethod = callMethod;
                            doAnotherCheck = false;
                        }
                    }
                    if (doAnotherCheck && method.DeclaringType.BaseType.IsTypeDef)
                    {
                        //TODO:TypeSpec for generic types
                        var baseMethod2 = ((TypeDef)method.DeclaringType.BaseType).FindMethod("Finalize", baseMethod.MethodSig);
                        if (method.Access == MethodAttributes.Family && baseMethod2.IsVirtual)
                        {
                            baseMethod = baseMethod2;
                        }
                    }
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseMethod));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Endfinally));
                    method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
                    {
                        TryStart = method.Body.Instructions[method.Body.Instructions.Count - 4],
                        TryEnd = method.Body.Instructions[method.Body.Instructions.Count - 3],
                        HandlerStart = method.Body.Instructions[method.Body.Instructions.Count - 3],
                        HandlerEnd = retn,
                    });
                }
                method.Body.Instructions.Add(retn);
            }

            method.Body.UpdateInstructionOffsets();
        }
        private class ErrorLogger : ILogger
        {
            public static ErrorLogger Instance { get; } = new ErrorLogger();
            private ErrorLogger() { }

            public bool IgnoresEvent(LoggerEvent loggerEvent)
            {
                return loggerEvent >= LoggerEvent.Info;
            }

            public void Log(object sender, LoggerEvent loggerEvent, string format, params object[] args)
            {
                string msg = $"{loggerEvent}: {string.Format(format, args)}";

                Console.Error.WriteLine(msg);
            }
    }
}
