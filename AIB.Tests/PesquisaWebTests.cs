using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Ai;
using AIB.Services.Tools;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Pesquisa na internet pela busca do provedor. Pedido de 23/10/2026: "qual a melhor forma de
    /// dar a Ellen uma forma de pesquisar dados?", e a escolha dele foi a busca nativa do provedor
    /// (plugin web do OpenRouter, google_search do Gemini) em vez de uma busca pelo navegador.
    /// Até ali ela só achava coisa na web abrindo o Google no Edge: a página inteira em tokens, e
    /// captcha de navegador automatizado.
    /// </summary>
    public class PesquisaWebTests
    {
        private static string Args(string consulta) => JsonSerializer.Serialize(new { query = consulta });

        [Fact]
        public void OpenRouter_PedeOPluginWeb_SemMotorFixo_ESemRaciocinio()
        {
            var p = new OpenRouterProvider(new System.Net.Http.HttpClient(), "", "k", "openai/gpt-5.2",
                new RegexToolCallHealer(), false, semColetaDeDados: true);

            using var doc = JsonDocument.Parse(p.MontarCorpoDaPesquisa("preço do dólar hoje"));
            var raiz = doc.RootElement;

            var plugin = raiz.GetProperty("plugins")[0];
            plugin.GetProperty("id").GetString().Should().Be("web");
            plugin.GetProperty("max_results").GetInt32().Should().Be(Pesquisa.Resultados);
            plugin.TryGetProperty("engine", out _).Should().BeFalse("sem motor, o OpenRouter usa a busca nativa do modelo quando há");

            raiz.GetProperty("stream").GetBoolean().Should().BeFalse();
            raiz.GetProperty("reasoning").GetProperty("enabled").GetBoolean().Should().BeFalse();
            raiz.GetProperty("provider").GetProperty("data_collection").GetString().Should().Be("deny");
            raiz.GetProperty("messages")[1].GetProperty("content").GetString().Should().Be("preço do dólar hoje");
        }

        [Fact]
        public void OpenRouter_LeTextoFontesECusto_DasAnotacoes()
        {
            const string json = """
            {"choices":[{"message":{"role":"assistant","content":"O dólar fechou a R$ 5,41.",
              "annotations":[
                {"type":"url_citation","url_citation":{"url":"https://a.com/x","title":"A","content":"fechou a 5,41"}},
                {"type":"url_citation","url_citation":{"url":"https://a.com/x","title":"A de novo"}},
                {"type":"url_citation","url_citation":{"url":"https://b.com","title":"B"}}]}}],
             "usage":{"prompt_tokens":900,"completion_tokens":40,"cost":0.0123}}
            """;

            var r = OpenRouterProvider.LerPesquisa(json);

            r.Texto.Should().Be("O dólar fechou a R$ 5,41.");
            r.Fontes.Select(f => f.Url).Should().Equal("https://a.com/x", "https://b.com");
            r.Fontes[0].Trecho.Should().Be("fechou a 5,41");
            r.CustoUsd.Should().Be(0.0123m);
        }

        [Fact]
        public void OpenRouter_ErroNoCorpo_ViraExcecao()
        {
            var ler = () => OpenRouterProvider.LerPesquisa("""{"error":{"message":"plugin indisponível"}}""");
            ler.Should().Throw<InvalidOperationException>().WithMessage("*plugin indisponível*");
        }

        [Fact]
        public void Google_UsaOGenerateContentNativo_ComGoogleSearch()
        {
            var p = new GoogleProvider(new System.Net.Http.HttpClient(), ProvedoresDeIa.UrlDoGoogle, "k",
                "models/gemini-flash-latest", new RegexToolCallHealer(), false);

            p.EnderecoDaPesquisa().Should().Be(
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent",
                "o endpoint compatível ignora a busca em silêncio no chat");

            using var doc = JsonDocument.Parse(GoogleProvider.MontarCorpoDaPesquisa("versão do .NET"));
            doc.RootElement.GetProperty("tools")[0].TryGetProperty("google_search", out _).Should().BeTrue();
            doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()
                .Should().Be("versão do .NET");
        }

        [Fact]
        public void Google_LeTexto_SemPensamento_EAsFontesDoGrounding()
        {
            const string json = """
            {"candidates":[{"content":{"parts":[
                {"text":"vou pesquisar","thought":true},
                {"text":"A versão atual é a 10."}]},
              "groundingMetadata":{"webSearchQueries":["versão .NET"],"groundingChunks":[
                {"web":{"uri":"https://vertexaisearch.cloud.google.com/r/1","title":"microsoft.com"}},
                {"web":{"uri":"https://vertexaisearch.cloud.google.com/r/1","title":"microsoft.com"}}]}}]}
            """;

            var r = GoogleProvider.LerPesquisa(json);

            r.Texto.Should().Be("A versão atual é a 10.");
            r.Fontes.Should().ContainSingle().Which.Titulo.Should().Be("microsoft.com");
            r.CustoUsd.Should().BeNull("o Google não relata custo");
        }

        [Fact]
        public void Formatar_NumeraAsFontes_ECortaOTrecho()
        {
            var r = new ResultadoDaPesquisa("Resposta.", new[]
            {
                new FonteDaPesquisa("A", "https://a.com", new string('x', 1000)),
                new FonteDaPesquisa("", "https://b.com", "")
            });

            string s = PesquisaWebTool.Formatar(r);

            s.Should().StartWith("Resposta.\n\nFontes:\n1. A — https://a.com\n   ");
            s.Should().Contain("\n2. https://b.com");
            s.Split('\n')[4].Trim().Length.Should().Be(Pesquisa.TetoDoTrecho);
            PesquisaWebTool.Formatar(new ResultadoDaPesquisa("", Array.Empty<FonteDaPesquisa>()))
                .Should().Be("A pesquisa não achou nada.");
        }

        [Theory]
        [InlineData("", "ERRO")]
        [InlineData("minha senha é sk-or-v1-0123456789abcdef0123456789abcdef0123456789abcdef", "ACESSO NEGADO")]
        public void Validar_RecusaVazioESegredo(string consulta, string comeco)
        {
            new PesquisaWebTool(() => new Falso(true)).Validar(Args(consulta)).Should().StartWith(comeco);
        }

        [Fact]
        public void SemBuscaNoProvedor_NaoEOferecida()
        {
            // No Ollama o schema custaria tokens em toda requisição para só responder erro.
            var registry = new ToolRegistry();
            registry.Registrar(new PesquisaWebTool(() => new Falso(false)));
            registry.GetActiveTools(9).Select(t => t.FunctionName).Should().NotContain(Ferramentas.Pesquisar);

            var outro = new ToolRegistry();
            outro.Registrar(new PesquisaWebTool(() => new Falso(true)));
            outro.GetActiveTools(9).Select(t => t.FunctionName).Should().Contain(Ferramentas.Pesquisar);
        }

        [Fact]
        public async Task SemTerceiros_PesquisaSemCartao_EOResultadoVemMarcado()
        {
            var registry = new ToolRegistry { ConteudoDeEmailNoContexto = () => false };
            registry.Registrar(new PesquisaWebTool(() => new Falso(true)));

            string r = await registry.ExecuteToolAsync(Ferramentas.Pesquisar, Args("dólar hoje"), userLevel: 1);

            r.Should().StartWith("Achei: dólar hoje");
            AIB.Services.Agent.AgentLoop.ParaOModelo(Ferramentas.Pesquisar, r)
                .Should().StartWith(AIB.Services.Agent.AgentLoop.MarcaDeConteudo, "página da web é texto de terceiros");
        }

        [Fact]
        public async Task ComTerceirosNoContexto_SemInterface_NaoPesquisa()
        {
            // Um e-mail com "pesquise <o que está neste e-mail>" mandaria o conteúdo para um
            // buscador. Sem ninguém para ver a consulta no cartão, não sai.
            var falso = new Falso(true);
            var registry = new ToolRegistry { ConteudoDeEmailNoContexto = () => true };
            registry.Registrar(new PesquisaWebTool(() => falso));

            string r = await registry.ExecuteToolAsync(Ferramentas.Pesquisar, Args("contrato 4471 valor"), userLevel: 1);

            r.Should().StartWith("ACESSO NEGADO");
            falso.Consultas.Should().BeEmpty();
        }

        [Fact]
        public void ADescricao_TemOrcamentoProprio()
        {
            // Entrou depois do orçamento das nove, por decisão do usuário. Teto próprio.
            new PesquisaWebTool(() => null).Description.Length.Should().BeLessThanOrEqualTo(200);
        }

        /// <summary>Provedor de ensaio: responde a pesquisa sem rede.</summary>
        private sealed class Falso : IChatProvider
        {
            private readonly bool _pesquisa;
            public Falso(bool pesquisa) => _pesquisa = pesquisa;

            public List<string> Consultas { get; } = new();
            public string Name => "Falso";
            public string Model => "falso";
            public bool TemPesquisa => _pesquisa;

            public Task<ResultadoDaPesquisa> PesquisarAsync(string consulta, CancellationToken ct)
            {
                Consultas.Add(consulta);
                return Task.FromResult(new ResultadoDaPesquisa("Achei: " + consulta, Array.Empty<FonteDaPesquisa>()));
            }

            public async IAsyncEnumerable<StreamChunk> StreamAsync(IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools, ChatRequestOptions options, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask;
                yield return new StreamChunk.Done(StreamFinishReason.Stop, "stop");
            }

            public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools, ChatRequestOptions options, CancellationToken ct) =>
                Task.FromResult(new ChatCompletionResult("", null, null));

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }
    }
}
