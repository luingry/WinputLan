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
            var root = Path.Combine(Path.GetTempPath(), "WinputLan-report-test-" + Guid.NewGuid().ToString("N"));
            try {
                var config = WinputConfig.CreateDefault(); config.DisplayName = "PRIVATE_MACHINE"; config.RemoteAddress = "192.168.87.91"; config.PinnedSecret = Encoding.UTF8.GetBytes("PRIVATE_SECRET");
                var log = new InMemoryTransactionLog(); log.Add("PRIVATE_MACHINE", "PRIVATE_IP", "Transport", "failed PRIVATE_SECRET"); log.Add("local","remote","Input.KeyDown","received");
                var report = BugReportDiagnostics.Capture(config,log.Snapshot(),new double[]{1,2,3,4},false,false,false,"Offline","Offline",1,1);
                var serialized = Encoding.UTF8.GetString(BugReportJson.Serialize(report));
                if(serialized.Contains("PRIVATE_") || serialized.Contains("192.168.") || serialized.Contains("PinnedSecret"))throw new Exception("Private values leaked.");
                report.Title="Teste diagnóstico";report.Description="Problema de teste detalhado.";
                var draft = BugReportDraft.Create(report); var store = new BugReportDraftStore(root); store.Save(draft);
                if(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"report-draft.dat"))).Contains(report.Title))throw new Exception("Draft is not encrypted.");
                var restored = store.Load(); if(restored.Secret!=draft.Secret||restored.Report.Id!=draft.Report.Id)throw new Exception("Draft identity lost.");
                restored.Frozen=true;store.Save(restored);if(!store.Load().Frozen)throw new Exception("Uncertain send state lost.");
                var fake = new FakeHttp(draft.Report.Id);
                using(var client=new BugReportClient(fake)) {
                    var receipt=client.SendAsync(draft,"token",CancellationToken.None).GetAwaiter().GetResult();
                    if(receipt!="WLR-"+report.Id||fake.Bodies.Count!=2||fake.Bodies[0]!=fake.Bodies[1])throw new Exception("Retries changed report identity/content.");
                }
                using(var client=new BugReportClient(new OversizedHttp())) {
                    try {client.SendAsync(draft,"token",CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Oversized response accepted.");}catch(InvalidOperationException){ }
                }
                var large = new BugReport { Title = "Teste de tamanho", Description = new string('\u4e00',6000), Steps = new string('\u4e00',4000) };
                large.Diagnostics.Events = Enumerable.Range(0,500).Select(_ => new BugReportEvent { Time=large.CapturedUtc, Origin="remote", Destination="remote", Type="Input.ReleaseAll", Status="code-renewed-trust-cleared" }).ToList();
                if(BugReportJson.Validate(large)!=null)throw new Exception("UTF-8 fixture is outside the character limits.");
                try { BugReportJson.Serialize(large);throw new Exception("Oversized restart fixture is too small."); }catch(InvalidOperationException){ }
                var untouched = new FakeHttp(large.Id);
                using(var client=new BugReportClient(untouched)) {
                    try { client.SendAsync(BugReportDraft.Create(large),"token",CancellationToken.None).GetAwaiter().GetResult();throw new Exception("UTF-8 body limit was bypassed."); }catch(InvalidOperationException){ }
                }
                if(untouched.Bodies.Count!=0)throw new Exception("Oversized UTF-8 report reached HTTP.");
                var fresh = BugReportJson.NewFromSnapshot(large);
                if(fresh.Id==large.Id || fresh.Title!="" || fresh.Description!="" || fresh.Steps!="" || fresh.Diagnostics.Events.Count!=500 || ReferenceEquals(fresh.Diagnostics,large.Diagnostics))throw new Exception("Oversized draft could not restart with isolated diagnostics.");
                store.Clear();Console.WriteLine("REPORT PASS: allowlist privacy, DPAPI draft, frozen recovery, identical retry, UTF-8 limits, oversized restart and bounded response");
            } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
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
