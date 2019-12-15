using CommandLine;

namespace ReferenceAssemblyGenerator.CLI
{
    public class ProgramOptions
    {
        [Option('o', "output", Required = false, HelpText = "Sets the output file")]
        public string OutputFile { get; set; }

        [Option('f', "force", Required = false, HelpText = "Overrides output file if it exists")]
        public bool Force { get; set; }

        [Option("keep-non-public", Required = false, HelpText = "Sets if non-public metadata should be kept")]
        public bool KeepNonPublic { get; set; }

        [Option("keep-internal", Required = false, Default = (byte)2, HelpText = "Sets if internal metadata should be kept. (0: disable, 1: enable, 2: auto(default))")]
        public byte KeepInternal { get; set; }

        [Option("inject-reference-assembly-attribute", Required = false, HelpText = "Inject ReferenceAssemblyAttribute")]
        public bool InjectReferenceAssemblyAttribute { get; set; }

        [Option("use-runtime-mode", Required = false, HelpText = "Only use throw when needed, and change extern methods to normal")]
        public bool UseRuntimeMode { get; set; }

        [Option("use-ret", Required = false, HelpText = "Uses empty returns instead of throw null")]
        public bool UseRet { get; set; }

        [Value(0, MetaName = "assemblyPath", Required = true, HelpText = "Path to assembly to generate reference assembly for.")]
        public string AssemblyPath { get; set; }
    }
}