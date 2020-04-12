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
        private const string CompilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

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

            if (string.IsNullOrEmpty(opts.OutputFile))
            {
                string fileName = Path.GetFileNameWithoutExtension(opts.AssemblyPath);
                string extension = Path.GetExtension(opts.AssemblyPath);

                opts.OutputFile = opts.AssemblyPath.Replace(fileName + extension, fileName + "-reference" + extension);
            }


            if (Directory.Exists(opts.AssemblyPath))
            {
                var baseLen = Path.GetFullPath(opts.AssemblyPath).Length + 1;
                foreach (var path in Directory.EnumerateFiles(opts.AssemblyPath, "*.dll", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(opts.AssemblyPath, "*.exe", SearchOption.AllDirectories))
                    .Concat(Directory.EnumerateFiles(opts.AssemblyPath, "*.winmd", SearchOption.AllDirectories)))
                {
                    ProcessSingleFileSafe(path, Path.Combine(opts.OutputFile, path.Substring(baseLen)));
                }
            }
            else
            {
                if (!File.Exists(opts.AssemblyPath))
                {
                    throw new FileNotFoundException("Assembly file was not found", opts.AssemblyPath);
                }
                var outputDir = Path.GetDirectoryName(opts.OutputFile);
                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);
                ProcessSingleFileSafe(opts.AssemblyPath, opts.OutputFile);
            }
        }
        private static void ProcessSingleFileSafe(string input, string output)
        {
            byte keepInternal = s_ProgamOptions.KeepInternal;
            try
            {
                ProcessSingleFile(input, output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: Process file '{input}' failed. <- {ex}");
            }
            finally
            {
                //Reset KeepInternal options for auto mode
                s_ProgamOptions.KeepInternal = keepInternal;
            }
        }

        private static void ProcessSingleFile(string input, string output)
        {
            if (File.Exists(output) && !s_ProgamOptions.Force)
            {
                throw new Exception($"Output file '{input}' exists already. Use --force to override it.");
            }

            byte[] assemblyData = File.ReadAllBytes(input);

            using (MemoryStream inputStream = new MemoryStream(assemblyData))
            {
                do
                {
                    inputStream.Position = 0;
                    ModuleDefMD module;
                    try
                    {
                        module = ModuleDefMD.Load(inputStream);
                    }
                    catch (BadImageFormatException ex)
                    {
                        Console.WriteLine($"Info: Skip non-module file '{input}' <- {ex.Message}");
                        return;
                    }
                    if (s_ProgamOptions.KeepInternal == 2 && !module.Assembly.CustomAttributes.IsDefined("System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
                        s_ProgamOptions.KeepInternal = 0;

                    module.IsILOnly = true;
                    module.VTableFixups = null;
                    if (module.IsStrongNameSigned && !s_ProgamOptions.DelaySign)
                    {
                        module.IsStrongNameSigned = false;
                        module.Assembly.PublicKey = null;
                        module.Assembly.HasPublicKey = false;
                    }
                    if (!s_ProgamOptions.KeepResource)
                        module.Resources.Clear();

                    try
                    {
                        CheckTypes(module.Types);

                        CheckCustomAttributes(module.Assembly.CustomAttributes);
                        CheckCustomAttributes(module.CustomAttributes);
                        if (s_ProgamOptions.InjectReferenceAssemblyAttribute)
                            InjectReferenceAssemblyAttribute(module);
                    }
                    catch (TryAgainException)
                    {
                        continue;
                    }

                    if (File.Exists(output))
                    {
                        File.Delete(output);
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
                        moduleOpts.MetadataOptions.Flags |= dnlib.DotNet.Writer.MetadataFlags.RoslynSortInterfaceImpl;
                        module.Write(outputStream, moduleOpts);
                        outputStream.Position = 0;
                        if (!Directory.Exists(Path.GetDirectoryName(output)))
                            Directory.CreateDirectory(Path.GetDirectoryName(output));
                        using (var fileStream = File.Create(output))
                        {
                            outputStream.CopyTo(fileStream);
                        }
                    }
                    break;
                } while (false);
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
            Assert(type.BaseType == null || IsReachable(type.BaseType), type);
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
                                if (new SigComparer().Equals(mo.MethodDeclaration.DeclaringType, @interface.Interface))
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
                        Assert(IsReachable(mp.Type), method);
                    }
                    if (method.Parameters.ReturnParameter.ParamDef != null)
                        CheckCustomAttributes(method.Parameters.ReturnParameter.ParamDef.CustomAttributes);
                    Assert(IsReachable(method.Parameters.ReturnParameter.Type), method);

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
                    Assert(IsReachable(field.FieldType), field);
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
                    Assert(IsReachable(@event.EventType), @event);
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
            if (t.Visibility != TypeAttributes.NotPublic)
                return false;
            if (!t.FullName.EndsWith("Attribute", StringComparison.Ordinal))
                return false;
            if (t.FullName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal))
                return !t.Module.Name.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal);
            if (t.FullName.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal))
                return !t.Module.Name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal);
            if (t.FullName.StartsWith("System.Diagnostics.CodeAnalysis.", StringComparison.Ordinal))
                return !t.Module.Name.StartsWith("System.Diagnostics.CodeAnalysis.", StringComparison.Ordinal);
            return false;
        }

        private static bool IsReachable(TypeDef t, bool doNestedCheck)
        {
            if (s_ProgamOptions.KeepNonPublic)
                return true;
            if (t?.Module == null)
                return false;
            //if (t.IsGlobalModuleType)
            //    return true;
            //Skip compile inject internal attributes
            if (IsWellKnownCompilerInjectedType(t))
                return true;
            if (s_ProgamOptions.KeepInternal == 2 &&
                (t.Visibility == TypeAttributes.NestedAssembly || t.Visibility == TypeAttributes.NotPublic) &&
                t.CustomAttributes.IsDefined(CompilerGeneratedAttribute))
                return false;
            bool isReachable;
            //IsReachable for DeclaringType was checked in outter loop.
            switch (t.Visibility)
            {
                case TypeAttributes.Public://public
                    return true;
                case TypeAttributes.NestedPublic://public
                    isReachable = true && (!doNestedCheck | IsReachable(t.DeclaringType, true));
                    break;
                case TypeAttributes.NestedFamily://protected
                case TypeAttributes.NestedFamORAssem://protected internal
                    isReachable = true && (!doNestedCheck || IsReachable(t.DeclaringType, true));
                    break;
                case TypeAttributes.NotPublic://internal
                    {
                        isReachable = s_ProgamOptions.KeepInternal != 0;
                    }
                    break;
                case TypeAttributes.NestedAssembly://internal
                case TypeAttributes.NestedFamANDAssem://private protected
                    isReachable = s_ProgamOptions.KeepInternal != 0 && (!doNestedCheck || IsReachable(t.DeclaringType, true));
                    break;
                case TypeAttributes.NestedPrivate://private
                    isReachable = false;
                    break;
                default:
                    throw new InvalidOperationException($"Unreachable code. <-IsReachable(TypeDef.Visibility: {t.Visibility})");
            }
            if (!isReachable && t.Module.EntryPoint != null)
            {
                //TODO: Can only keep the method and remove all other members in the type.
                var type = t.Module.EntryPoint.DeclaringType;

                while (type != null)
                {
                    if (new SigComparer().Equals(type, t))
                        return true;
                    type = type.DeclaringType;
                }
            }
            return isReachable;
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
            return t != null && t.Module != null;
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
            if (f?.Module == null)
                return false;
            if (s_ProgamOptions.KeepInternal == 2 &&
                f.Access == FieldAttributes.Assembly &&
                f.CustomAttributes.IsDefined(CompilerGeneratedAttribute))
                return false;
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
            if (m?.Module == null)
                return false;
            if (m.IsStaticConstructor)
                return false;
            if (s_ProgamOptions.KeepInternal == 2 &&
                m.Access == MethodAttributes.Assembly &&
                m.CustomAttributes.IsDefined(CompilerGeneratedAttribute) &&
                !m.HasOverrides)
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
            //Keep `Main()` as EntryPoint
            if (!isReachable && m == m.Module.EntryPoint)
                return true;
            return isReachable;
        }

        private static bool IsReachable(CAArgument attrArg)
        {
            if (!IsReachable(attrArg.Type))
                return false;

            if (attrArg.Value is TypeSig)
            {
                return IsReachable((TypeSig)attrArg.Value);
            }
            if (attrArg.Value is List<CAArgument>)
            {
                return ((List<CAArgument>)attrArg.Value).All(ca => IsReachable(ca));
            }
            return true;
        }

        private static bool IsReachable(CustomAttribute attr)
        {
            if (!IsReachable(attr.AttributeType))
                return false;
            if (attr.Constructor.IsMethodDef && !IsReachable((MethodDef)attr.Constructor))
                return false;
            //Verify there is no unreachable type(typeof or Enum value) in all args(and array or nested array in args).
            if (!attr.ConstructorArguments.All(ca => IsReachable(ca)))
                return false;
            if (!attr.NamedArguments.All(na => IsReachable(na.Type) && IsReachable(na.Argument)))
                return false;
            //TODO: It can be field/prop in base types, so maybe can't do verify on it, since the field/prop maybe already removed here, and can not be diff from exists in baseType?
            //Verify there is no unreachable fields or properties
            //if (attr.AttributeType.IsTypeDef && !attr.NamedArguments.All(na => na.IsField ? IsReachable(((TypeDef)attr.AttributeType).GetField(na.Name)) : IsReachable(((TypeDef)attr.AttributeType).FindProperty(na.Name)?.SetMethod)))
            //    return false;

            return true;
        }


        private static void CheckCustomAttributes(CustomAttributeCollection collection)
        {
            if (collection.Count == 0)
                return;
            foreach (var attr in collection.ToArray())
            {
                if (!IsReachable(attr))
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
                        if (baseTypeCtorRef == null && oldBody != null && oldBody.Instructions.Count >= 3)
                        {
                            // <field init>, ldarg_0, call, <ctor>, retn
                            for (int i = 0; i < oldBody.Instructions.Count - 2; ++i)
                            {
                                var inst0 = oldBody.Instructions[i];
                                var inst1 = oldBody.Instructions[i + 1];
                                if (inst0.IsLdarg() && inst0.GetParameterIndex() == 0 &&
                                    inst1.OpCode == OpCodes.Call && inst1.Operand is IMethod ctorMethod &&
                                    new SigComparer().Equals(ctorMethod.DeclaringType, method.DeclaringType.BaseType) && ctorMethod.Name == ".ctor" &&
                                    ctorMethod.MethodSig.Params.Count == 0 && ctorMethod.MethodSig.GenParamCount == 0)
                                {
                                    baseTypeCtorRef = ctorMethod;
                                    break;
                                }
                            }
                        }
                        if (baseTypeCtorRef == null && new SigComparer().Equals(method.DeclaringType.BaseType.ToTypeSig(), method.Module.CorLibTypes.Object))
                        {
                            baseTypeCtorRef = new MemberRefUser(method.Module, ".ctor", MethodSig.CreateInstance(method.Module.CorLibTypes.Void), method.DeclaringType.BaseType);
                        }
                        if (baseTypeCtorRef == null)
                        {
                            //Don't want to look into references, since reference assemblies not always available and match
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
                    bool doAnotherCheck = !new SigComparer().Equals(method.DeclaringType.BaseType, baseMethod.DeclaringType);
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
                        if (baseMethod2 != null && baseMethod2.Access == MethodAttributes.Family && baseMethod2.IsVirtual)
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

        [Conditional("DEBUG")]
        private static void Assert(bool flag, IMemberRef refer)
        {
            //internal refs used by public ones maybe cause by ILMerge.
            if (flag)
                return;

            Console.Error.WriteLine($"Warning: Assert failed when process with '[{refer.Module.Name}]{refer.FullName}'");
            if (!s_ProgamOptions.KeepNonPublic && s_ProgamOptions.KeepInternal != 1)
            {
                bool tryAgain = false;
                if (s_ProgamOptions.KeepInternal == 2 || s_ProgamOptions.KeepInternal == 3)
                {
                    s_ProgamOptions.KeepInternal = 1;
                    tryAgain = true;
                }
                else if (s_ProgamOptions.KeepInternal == 0)
                {
                    s_ProgamOptions.KeepInternal = 3;
                    tryAgain = true;
                }
                if (tryAgain)
                {
                    Console.Error.WriteLine($"Warning: Maybe caused by ILMerge, try again with KeepInternal={s_ProgamOptions.KeepInternal}");
                    throw new TryAgainException();
                }
            }
            if (Debugger.IsAttached)
                Debugger.Break();
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

                var type = TryReportLastTypeDef(sender, out var module);
                if (module != null)
                    msg += $" <- for module '{module.Name}'";
                if (!string.IsNullOrEmpty(type))
                    msg += $" <- Maybe happen near type '{type}'";

                Console.Error.WriteLine(msg);
            }

            private static System.Reflection.MethodInfo mMetadata_GetAllTypeDefs;
            private static string TryReportLastTypeDef(object sender, out ModuleDef module)
            {
                module = null;
                if (sender is dnlib.DotNet.Writer.ModuleWriter mw)
                    sender = mw.Metadata;
                if (sender is dnlib.DotNet.Writer.Metadata writer)
                {
                    module = writer.Module;
                    dnlib.DotNet.MD.RawTypeDefRow cur = default;
                    int row;
                    for (row = writer.TablesHeap.TypeDefTable.Rows; row > 0; --row)
                    {
                        cur = writer.TablesHeap.TypeDefTable[(uint)row];
                        if (cur.Name != 0)
                            break;
                    }
                    try
                    {
                        mMetadata_GetAllTypeDefs = mMetadata_GetAllTypeDefs ?? typeof(dnlib.DotNet.Writer.Metadata).GetMethod("GetAllTypeDefs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

                        TypeDef[] typeDefs = (TypeDef[])mMetadata_GetAllTypeDefs.Invoke(writer, Array.Empty<object>());
                        var type = typeDefs.Where(def => writer.GetRid(def) == row).FirstOrDefault();
                        if (type != null)
                        {
                            return type.FullName;
                        }
                    }
                    catch
                    {
                        //ignore all Exceptions
                    }
                }
                return null;
            }
        }

        public class TryAgainException : Exception
        {
            public TryAgainException()
            {
            }
        }
    }
}
