using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WinputLan.Core;

namespace WinputLan.Runtime
{
    [DataContract]
    public sealed class BugReportResponse
    {
        [DataMember(Name = "accepted")] public bool Accepted { get; set; }
        [DataMember(Name = "receipt")] public string Receipt { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
    }
    public sealed class BugReportClient : IDisposable
    {
        public static readonly Uri ServiceUri = new Uri("https://bugs.luingry.com.br/");
        private readonly HttpClient _http;
        public BugReportClient() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
        public BugReportClient(HttpMessageHandler handler) { _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) }; }
        public async Task<string> SendAsync(BugReportAttempt attempt, string token, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            cancellationToken = timeout.Token;
            var data = BugReportJson.Serialize(new BugReportSubmission { Report = attempt.Report, Secret = attempt.Secret, Token = token ?? "" });
            // Retrying after a lost response keeps identity/content unchanged. No background resend loop.
            for (var retry = 0; ; retry++)
            {
                try {
                    using (var message = new HttpRequestMessage(HttpMethod.Post, new Uri(ServiceUri, "api/reports")))
                    {
                        var body = new ByteArrayContent(data); message.Content = body;
                        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                        using (var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                        {
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new MemoryStream())
                            {
                                var buffer = new byte[1024]; int read;
                                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0) { if (output.Length + read > 4096) throw new InvalidOperationException("Resposta inválida do serviço."); output.Write(buffer, 0, read); }
                                var result = BugReportJson.Deserialize<BugReportResponse>(output.ToArray());
                                if (response.IsSuccessStatusCode && result.Accepted && result.Receipt == "WLR-" + attempt.Report.Id) return result.Receipt;
                                if ((int)response.StatusCode == 429) throw new InvalidOperationException("Limite de envios atingido. Tente mais tarde.");
                                if ((int)response.StatusCode == 403) throw new InvalidOperationException("Aguarde a verificação de segurança e tente novamente.");
                                if ((int)response.StatusCode == 409) throw new InvalidOperationException("O relato já existe com outro conteúdo. Feche e reabra esta janela.");
                                throw new InvalidOperationException("Não foi possível confirmar o envio. Tente novamente nesta janela.");
                            }
                        }
                    }
                } catch (HttpRequestException) when (retry == 0) { await Task.Delay(1500, cancellationToken).ConfigureAwait(false); }
            }
            }
        }
        public void Dispose() { _http.Dispose(); }
    }
}
