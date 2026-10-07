using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using WinputLan.Core;
using WinputLan.Runtime;

namespace WinputLan.Loopback
{
    internal static class BugReportTests
    {
        public static void Run()
        {
            {
                var config = WinputConfig.CreateDefault(); config.DisplayName = "PRIVATE_MACHINE"; config.RemoteAddress = "192.168.87.91"; config.PinnedSecret = Encoding.UTF8.GetBytes("PRIVATE_SECRET");
                var log = new InMemoryTransactionLog(); log.Add("PRIVATE_MACHINE", "PRIVATE_IP", "Transport", "failed PRIVATE_SECRET"); log.Add("local","remote","Input.KeyDown","received");
                var report = BugReportDiagnostics.Capture(config,log.Snapshot(),new double[]{1,2,3,4},false,false,false,"Offline","Offline",1,1);
                var serialized = Encoding.UTF8.GetString(BugReportJson.Serialize(report));
                if(serialized.Contains("PRIVATE_") || serialized.Contains("192.168.") || serialized.Contains("PinnedSecret"))throw new Exception("Private values leaked.");
                report.Title="Teste diagnóstico";report.Description="Problema de teste detalhado.";
                var attempt = BugReportAttempt.Create(report);
                var fake = new FakeHttp(attempt.Report.Id);
                using(var client=new BugReportClient(fake)) {
                    var receipt=client.SendAsync(attempt,"token",CancellationToken.None).GetAwaiter().GetResult();
                    if(receipt!="WLR-"+report.Id||fake.Bodies.Count!=2||fake.Bodies[0]!=fake.Bodies[1])throw new Exception("Retries changed report identity/content.");
                }
                using(var client=new BugReportClient(new OversizedHttp())) {
                    try {client.SendAsync(attempt,"token",CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Oversized response accepted.");}catch(InvalidOperationException){ }
                }
                var boundary = new BugReport { Title = "Teste de limite", Description = new string('d',1000), Steps = new string('s',1000) };
                if(BugReportJson.Validate(boundary)!=null)throw new Exception("1000-character fields were rejected.");
                boundary.Description += "d";
                if(BugReportJson.Validate(boundary)==null)throw new Exception("1001-character description was accepted.");
                boundary.Description = new string('d',1000); boundary.Steps += "s";
                if(BugReportJson.Validate(boundary)==null)throw new Exception("1001-character steps were accepted.");
                // Deliberately exceeds field limits to keep the independent UTF-8 serializer guard covered.
                var large = new BugReport { Title = "Teste de tamanho", Description = new string('\u4e00',6000), Steps = new string('\u4e00',4000) };
                large.Diagnostics.Events = Enumerable.Range(0,500).Select(_ => new BugReportEvent { Time=large.CapturedUtc, Origin="remote", Destination="remote", Type="Input.ReleaseAll", Status="code-renewed-trust-cleared" }).ToList();
                try { BugReportJson.Serialize(large);throw new Exception("Oversized restart fixture is too small."); }catch(InvalidOperationException){ }
                var untouched = new FakeHttp(large.Id);
                using(var client=new BugReportClient(untouched)) {
                    try { client.SendAsync(BugReportAttempt.Create(large),"token",CancellationToken.None).GetAwaiter().GetResult();throw new Exception("UTF-8 body limit was bypassed."); }catch(InvalidOperationException){ }
                }
                if(untouched.Bodies.Count!=0)throw new Exception("Oversized UTF-8 report reached HTTP.");
                var fresh = BugReportJson.NewFromSnapshot(large);
                if(fresh.Id==large.Id || fresh.Title!="" || fresh.Description!="" || fresh.Steps!="" || fresh.Diagnostics.Events.Count!=500 || ReferenceEquals(fresh.Diagnostics,large.Diagnostics))throw new Exception("Oversized draft could not restart with isolated diagnostics.");
                Console.WriteLine("REPORT PASS: allowlist privacy, identical in-memory retry, 1000-character field limits, UTF-8 limits, fresh report with isolated diagnostics and bounded response");
            }
        }
        public static void Runtime()
        {
            Console.WriteLine("REPORT RUNTIME architecture="+(Environment.Is64BitProcess?"x64":"x86"));
            try {Console.WriteLine("REPORT RUNTIME WebView2="+CoreWebView2Environment.GetAvailableBrowserVersionString());}
            catch(Exception ex) {Console.WriteLine("REPORT RUNTIME failure="+ex.GetType().FullName+" hresult="+ex.HResult.ToString("X")+" detail="+ex.Message);Environment.ExitCode=1;}
        }
        private sealed class FakeHttp : HttpMessageHandler
        {
            private readonly string _id;
            public readonly System.Collections.Generic.List<string> Bodies = new System.Collections.Generic.List<string>();
            public FakeHttp(string id) { _id=id; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync());if(Bodies.Count==1)throw new HttpRequestException("lost response");
                return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"accepted\":true,\"receipt\":\"WLR-"+_id+"\"}")};
            }
        }
        private sealed class OversizedHttp : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new string('x',10000))});
        }
    }
}
