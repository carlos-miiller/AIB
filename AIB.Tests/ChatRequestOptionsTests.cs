using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Ai;
using FluentAssertions;
using Moq;
using Moq.Protected;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A janela e o keep-alive EM VIGOR: um valor só, lido por todas as chamadas ao Ollama.
    /// <para>
    /// Eram quatro fontes. O turno levava o keep-alive da tela; o aquecimento forçava -1; o
    /// provider trocava o nulo do compactador, do título e da triagem por -1; e esses três
    /// mandavam os 32.768 fixos do padrão do record como janela. Com "5 minutos" ou 16k na tela,
    /// cada resumo recarregava o modelo e voltava a travá-lo na memória.
    /// </para>
    /// <para>
    /// Na coleção ContextoGlobal — que não roda em paralelo com ninguém — porque os valores em
    /// vigor são estáticos, e o ensaio muda os dois. Cada ensaio devolve os de antes.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class ChatRequestOptionsTests : IDisposable
    {
        private readonly int _janela = ChatRequestOptions.JanelaAtual;
        private readonly int _janelaDoOllama = ChatRequestOptions.JanelaDoOllama;
        private readonly int _keepAlive = ChatRequestOptions.KeepAliveAtual;
        private readonly LimitesDoProvedor _limites = LimitesDoProvedor.Atual;
        private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-opcoes-" + Guid.NewGuid().ToString("N"));

        public ChatRequestOptionsTests() => Directory.CreateDirectory(_pasta);

        public void Dispose()
        {
            ChatRequestOptions.JanelaAtual = _janela;
            ChatRequestOptions.JanelaDoOllama = _janelaDoOllama;
            ChatRequestOptions.KeepAliveAtual = _keepAlive;
            LimitesDoProvedor.Atual = _limites;
            try { Directory.Delete(_pasta, true); } catch { }
        }

        /// <summary>Ollama fingido que guarda cada corpo recebido e responde <paramref name="status"/>.</summary>
        private static HttpClient Ollama(List<string> corpos, HttpStatusCode status = HttpStatusCode.OK, string resposta =
            "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}")
        {
            var handler = new Mock<HttpMessageHandler>();
            handler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
                {
#pragma warning disable xUnit1031
                    string? corpo = request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                    if (corpo != null) corpos.Add(corpo);
                    return new HttpResponseMessage { StatusCode = status, Content = new StringContent(resposta) };
                });
            return new HttpClient(handler.Object);
        }

        private static OllamaProvider Provider(HttpClient http) =>
            new(new OllamaNativeClient(ProvedoresDeIa.UrlDoOllama, http), ProvedoresDeIa.UrlDoOllama,
                "modelo", new RegexToolCallHealer(), http, verboseLogging: false);

        [Fact]
        public void Salvar_POE_EmVigor_OKeepAliveEAJanelaDoPerfilDoOllama()
        {
            var servico = new SettingsService(Path.Combine(_pasta, "settings.json"));
            var s = new UserAppSettings
            {
                AiProvider = ProvedoresDeIa.Ollama, KeepAlive = "5m", ContextWindow = 16384, PerfisMigrados = true
            }.Sanear();

            servico.SaveSettings(s);

            ChatRequestOptions.KeepAliveAtual.Should().Be(300);
            ChatRequestOptions.JanelaDoOllama.Should().Be(16384);
            ChatRequestOptions.Default.KeepAliveSeconds.Should().Be(300, "o padrão leva o keep-alive em vigor");
            ChatRequestOptions.DeServico(400).NumCtx.Should().Be(16384, "resumo, título e triagem na janela da tela");
        }

        [Fact]
        public void ComAConversaNoOpenRouter_OsValoresDoOllama_SaoOsDoPerfilDele()
        {
            // A triagem pode continuar no Ollama. A janela da conversa (a do OpenRouter) não é a
            // que o Ollama deve receber: 128k numa máquina sem GPU é paginar a RAM.
            var servico = new SettingsService(Path.Combine(_pasta, "settings.json"));
            var s = new UserAppSettings { AiProvider = ProvedoresDeIa.Ollama, KeepAlive = "30m", ContextWindow = 16384, PerfisMigrados = true }.Sanear();

            var nuvem = ProvedoresDeIa.PerfilPadrao(ProvedoresDeIa.OpenRouter);
            nuvem.Modelo = "fornecedor/modelo";
            nuvem.JanelaDeContexto = 131072;
            s.Ativar(ProvedoresDeIa.OpenRouter, nuvem);

            servico.SaveSettings(s);

            ChatRequestOptions.JanelaAtual.Should().Be(131072);
            ChatRequestOptions.JanelaDoOllama.Should().Be(16384);
            ChatRequestOptions.KeepAliveAtual.Should().Be(1800);
        }

        [Fact]
        public async Task ChamadaSemKeepAlive_LEVA_OEmVigor_ENaoMaisMenosUm()
        {
            ChatRequestOptions.KeepAliveAtual = 300;
            ChatRequestOptions.JanelaDoOllama = 16384;

            var corpos = new List<string>();
            var provider = Provider(Ollama(corpos));

            // As opções do título, do resumo e da triagem: sem keep-alive próprio.
            await provider.CompleteAsync(new ChatMessage[] { ChatMessage.CreateUserMessage("oi") },
                Array.Empty<ChatTool>(), ChatRequestOptions.DeServico(24), CancellationToken.None);

            using var doc = JsonDocument.Parse(corpos.Should().ContainSingle().Subject);
            doc.RootElement.GetProperty("keep_alive").GetInt32().Should().Be(300);
            doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32().Should().Be(16384);
        }

        [Fact]
        public async Task OAquecimento_USA_OKeepAliveEmVigor()
        {
            // Forçava -1: quem escolhia "5 minutos" tinha o modelo travado desde a abertura.
            ChatRequestOptions.KeepAliveAtual = 60;

            var corpos = new List<string>();
            await Provider(Ollama(corpos, resposta: "{}")).WarmupAsync(CancellationToken.None);

            using var doc = JsonDocument.Parse(corpos.Should().ContainSingle().Subject);
            doc.RootElement.GetProperty("keep_alive").GetInt32().Should().Be(60);
        }

        [Fact]
        public async Task OAquecimento_ComErroDoOllama_NaoLanca_ENaoFingeSucesso()
        {
            // 404 é modelo não baixado. O aquecimento dizia "com sucesso" sem olhar o status, e o
            // primeiro turno falhava sem pista. Aqui, fora do paralelo, dá para ler o console.
            var provider = Provider(Ollama(new List<string>(), HttpStatusCode.NotFound,
                "{\"error\":\"model 'modelo' not found\"}"));

            var saida = new StringWriter();
            var anterior = Console.Out;
            Console.SetOut(saida);
            try
            {
                Func<Task> aquecer = () => provider.WarmupAsync(CancellationToken.None);
                await aquecer.Should().NotThrowAsync();
            }
            finally
            {
                Console.SetOut(anterior);
            }

            saida.ToString().Should().Contain("[WARMUP ERRO] 404").And.NotContain("com sucesso");
        }

        [Theory]
        [InlineData("1m", 60)]
        [InlineData("5m", 300)]
        [InlineData("30m", 1800)]
        [InlineData("-1", -1)]
        [InlineData("qualquer", -1)]
        [InlineData(null, -1)]
        public void AConversaoDoKeepAlive_EhUmaSo(string? valor, int segundos)
        {
            ChatRequestOptions.SegundosDeKeepAlive(valor).Should().Be(segundos);
            ConversationService.SegundosDeKeepAlive(valor).Should().Be(segundos, "o turno usa a mesma conversão");
        }
    }
}
