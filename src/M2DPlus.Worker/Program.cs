using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool SetDllDirectory(string path);
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length<2 || Console.ReadLine()!="contained")return 2;
            var system=Path.GetFullPath(args[1]);
            var managed=Directory.Exists(Path.Combine(system,"netcore"))?Path.Combine(system,"netcore"):system;
            var native=Path.GetFileName(system).Equals("netcore",StringComparison.OrdinalIgnoreCase)?Directory.GetParent(system)!.FullName:system;
            if(args.Length>2)
            {
                var resolver=Assembly.LoadFrom(args[2]);resolver.GetType("RhinoInside.Resolver",true)!.GetMethod("Initialize",[typeof(string)])!.Invoke(null,[system]);
            }
            else
            {
                // Resolve only the explicitly selected installed Rhino build, never an arbitrary search path.
                if(!SetDllDirectory(native))throw new System.ComponentModel.Win32Exception();
                Environment.SetEnvironmentVariable("PATH",native+Path.PathSeparator+Environment.GetEnvironmentVariable("PATH"));
                AssemblyLoadContext.Default.Resolving+=(_,name)=>
                {
                    var path=Path.Combine(managed,name.Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;
                };
            }
            using var pipe=new NamedPipeClientStream(".",args[0],PipeDirection.InOut,PipeOptions.Asynchronous);
            pipe.Connect(30000);
            WorkerHost.Run(pipe);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
