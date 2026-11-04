using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace UntamedLab.Testing
{
    // CI-only protocol fixture. It never starts QEMU or accesses guest disks.
    public sealed class NamedPipeFixture : IDisposable
    {
        readonly NamedPipeServerStream server;
        readonly Thread worker;
        readonly List<string> commands=new List<string>();
        readonly string output;
        readonly int greetingKind;
        public string Failure {get;private set;}
        public NamedPipeFixture(string name,string output,int greetingKind)
        {
            this.output=output;this.greetingKind=greetingKind;
            server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            worker=new Thread(Run);worker.IsBackground=true;worker.Start();
        }
        public string[] Commands {get{lock(commands)return commands.ToArray();}}
        public bool Finished(int milliseconds){return worker.Join(milliseconds);}
        void Run()
        {
            try
            {
                server.WaitForConnection();
                using(var reader=new StreamReader(server,new UTF8Encoding(false,true),false,1024,true))
                using(var writer=new StreamWriter(server,new UTF8Encoding(false),1024,true){AutoFlush=true})
                {
                    writer.WriteLine(greetingKind==1?"{\"not_qmp\":true}":greetingKind==2?new string('x',20000):"{\"QMP\":{\"version\":{}}}");
                    for(int i=0;i<3;i++)
                    {
                        string line=reader.ReadLine();if(line==null)return;
                        var command=Regex.Match(line,"\"execute\"\\s*:\\s*\"([a-z_-]+)\"");
                        var id=Regex.Match(line,"\"id\"\\s*:\\s*\"([a-z]+)\"");
                        if(!command.Success||!id.Success)throw new IOException("Invalid fixture request");
                        string verb=command.Groups[1].Value;
                        lock(commands)commands.Add(verb);
                        string expected=new[]{"qmp_capabilities","query-status","screendump"}[i];
                        if(verb!=expected)throw new IOException("Unexpected fixture command");
                        if(i==2)File.WriteAllBytes(output,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jXioAAAAASUVORK5CYII="));
                        if(i==1)writer.WriteLine("{\"event\":\"RESUME\",\"data\":{}}");
                        writer.WriteLine("{\"return\":"+(i==1?"{\"status\":\"running\"}":"{}")+",\"id\":\""+id.Groups[1].Value+"\"}");
                    }
                }
            }
            catch(Exception ex){Failure=ex.GetType().Name;}
            finally{server.Dispose();}
        }
        public void Dispose(){server.Dispose();worker.Join(5000);}
    }
}
