using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Ai;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Provedores, perfis e o OpenRouter. A configuração fingia que qualquer provedor era o Ollama
    /// com outra URL; cada um agora tem os seus campos e guarda os seus valores.
    /// </summary>
    public class ProvedoresDeIaTests
    {
        // ── Migração do arquivo antigo ───────────────────────────────────────

        [Fact]
        public void ProvedorAntigoComUrlDoOpenRouter_ViraOpenRouter()
        {
            var s = new UserAppSettings { AiProvider = "OpenAI", ApiUrl = "https://openrouter.ai/api/v1", ModelName = "x/y" }.Sanear();

            s.AiProvider.Should().Be(ProvedoresDeIa.OpenRouter);
            s.ModelName.Should().Be("x/y");
        }

        [Fact]
        public void ProvedorAntigoQualquer_ViraOllama_ComEnderecoLocal()
        {
            var s = new UserAppSettings { AiProvider = "Anthropic", ApiUrl = "https://api.anthropic.com/v1" }.Sanear();

            s.AiProvider.Should().Be(ProvedoresDeIa.Ollama);
            s.ApiUrl.Should().Be(ProvedoresDeIa.UrlDoOllama, "a URL de nuvem não serve ao Ollama");
        }

        [Fact]
        public void KeepAliveDeCincoMinutos_QueNuncaValeu_ViraSempreCarregado_UmaVezSo()
        {
            // "5m" era o padrão da tela e nunca chegou ao Ollama, que recebia -1. A migração
            // preserva o que acontecia; depois dela, "5m" escolhido de propósito fica.
            var s = new UserAppSettings { AiProvider = "Ollama", KeepAlive = "5m" }.Sanear();
            s.KeepAlive.Should().Be("-1");

            s.KeepAlive = "5m";
            s.Sanear().KeepAlive.Should().Be("5m");
        }

        [Fact]
        public void TriagemMigrada_SegueOQueJaAcontecia()
        {
            var s = new UserAppSettings { AiProvider = "OpenRouter", ApiUrl = ProvedoresDeIa.UrlDoOpenRouter, ModelName = "a/b" }.Sanear();

            s.MailTriageProvider.Should().Be(ProvedoresDeIa.OpenRouter);
            s.MailTriageModel.Should().Be("a/b");
        }

        // ── Perfis ───────────────────────────────────────────────────────────

        [Fact]
        public void TrocarDeProvedorEVoltar_TrazOPerfilDeVolta()
        {
            var s = new UserAppSettings { AiProvider = "Ollama", ModelName = "qwen3.5:4b", KeepAlive = "30m", ContextWindow = 16384 }.Sanear();

            var nuvem = s.PerfilDe(ProvedoresDeIa.OpenRouter);
            nuvem.Modelo = "deepseek/deepseek-chat";
            s.Ativar(ProvedoresDeIa.OpenRouter, nuvem);

            s.ModelName.Should().Be("deepseek/deepseek-chat");
            s.KeepAlive.Should().BeEmpty("keep-alive não existe no OpenRouter");

            s.Ativar(ProvedoresDeIa.Ollama, s.PerfilDe(ProvedoresDeIa.Ollama));

            s.ModelName.Should().Be("qwen3.5:4b");
            s.KeepAlive.Should().Be("30m");
            s.ContextWindow.Should().Be(16384);
            s.PerfilDe(ProvedoresDeIa.OpenRouter).Modelo.Should().Be("deepseek/deepseek-chat");
        }

        [Fact]
        public void Triagem_UsaOProvedorEOModeloDELA_NaoOsDaConversa()
        {
            var s = new UserAppSettings { AiProvider = "Ollama", ModelName = "qwen3.5:9b" }.Sanear();
            s.Ativar(ProvedoresDeIa.OpenRouter, new PerfilDeProvedor { Modelo = "deepseek/deepseek-chat" });
            s.MailTriageProvider = ProvedoresDeIa.Ollama;
            s.MailTriageModel = "";

            var triagem = s.ParaTriagem();

            triagem.AiProvider.Should().Be(ProvedoresDeIa.Ollama, "os e-mails ficam na máquina");
            triagem.ModelName.Should().Be("qwen3.5:9b", "modelo vazio usa o do perfil do Ollama");
            s.AiProvider.Should().Be(ProvedoresDeIa.OpenRouter, "a cópia não mexe na conversa");
        }

        [Fact]
        public void OpcoesDoTurno_LevamKeepAliveJanelaERaciocinioDoProvedor()
        {
            var ollama = new UserAppSettings { AiProvider = "Ollama", KeepAlive = "30m", ContextWindow = 16384 };
            ollama.PerfisMigrados = true;
            ollama.Sanear();

            var o = ConversationService.OpcoesDoTurno(ollama);
            o.KeepAliveSeconds.Should().Be(1800, "o keep-alive da tela nunca tinha chegado à requisição");
            o.NumCtx.Should().Be(16384);
            o.Raciocinio.Should().BeNull();

            var nuvem = new UserAppSettings { AiProvider = "OpenRouter", Reasoning = "high", PerfisMigrados = true }.Sanear();
            var n = ConversationService.OpcoesDoTurno(nuvem);
            n.KeepAliveSeconds.Should().BeNull();
            n.Raciocinio.Should().Be("high");
        }

        // ── OpenRouter: corpo, stream e catálogo ─────────────────────────────

        private static OpenRouterProvider Provedor() =>
            new(new System.Net.Http.HttpClient(), ProvedoresDeIa.UrlDoOpenRouter, "sk-or-x", "a/b", new RegexToolCallHealer(), false);

        private static JsonElement Corpo(Services.Ai.ChatRequestOptions opcoes, IReadOnlyList<ChatTool>? ferramentas = null) =>
            JsonDocument.Parse(Provedor().MontarCorpo(
                new ChatMessage[] { ChatMessage.CreateSystemMessage("s"), ChatMessage.CreateUserMessage("oi") },
                ferramentas ?? System.Array.Empty<ChatTool>(), opcoes, stream: true)).RootElement;

        [Fact]
        public void RaciocinioDesligado_VaiComoEnabledFalse_EEsforcoComoEffort()
        {
            Corpo(new ChatRequestOptions(Think: false)).GetProperty("reasoning").GetProperty("enabled").GetBoolean()
                .Should().BeFalse("o resumo e a triagem pedem Think=false, e aqui raciocínio é pago");

            Corpo(new ChatRequestOptions(Raciocinio: "medium")).GetProperty("reasoning").GetProperty("effort").GetString()
                .Should().Be("medium");

            Corpo(new ChatRequestOptions(Raciocinio: "model")).TryGetProperty("reasoning", out _)
                .Should().BeFalse("padrão do modelo não manda o campo");
        }

        [Fact]
        public void Corpo_TemMensagensFerramentasUsoETeto()
        {
            var ferramenta = ChatTool.CreateFunctionTool("read", "lê", System.BinaryData.FromString("{\"type\":\"object\"}"));
            var corpo = Corpo(new ChatRequestOptions(NumPredict: 400), new[] { ferramenta });

            corpo.GetProperty("model").GetString().Should().Be("a/b");
            corpo.GetProperty("messages")[1].GetProperty("role").GetString().Should().Be("user");
            corpo.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString().Should().Be("read");
            corpo.GetProperty("stream_options").GetProperty("include_usage").GetBoolean().Should().BeTrue();
            corpo.GetProperty("max_tokens").GetInt32().Should().Be(400);
        }

        [Fact]
        public void TrechoDoStream_SeparaTextoRaciocinioChamadaEUso()
        {
            var t = OpenRouterProvider.LerTrecho(
                """{"choices":[{"delta":{"content":"oi","reasoning":"pensando","tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{\"path\""}}]},"finish_reason":null}]}""");

            t.Texto.Should().Be("oi");
            t.Raciocinio.Should().Be("pensando", "é o que acende o indicador de pensando na tela");
            t.Chamadas.Should().ContainSingle().Which.Should().Be(new OpenRouterProvider.PedacoDeChamada(0, "c1", "read", "{\"path\""));

            var uso = OpenRouterProvider.LerTrecho(
                """{"choices":[],"usage":{"prompt_tokens":1200,"completion_tokens":80,"prompt_tokens_details":{"cached_tokens":1000}}}""").Uso!;
            uso.PromptEvalCount.Should().Be(1200);
            uso.EvalCount.Should().Be(80);
            uso.CachedTokens.Should().Be(1000);

            OpenRouterProvider.LerTrecho("""{"error":{"message":"Insufficient credits"}}""").Erro
                .Should().Be("Insufficient credits");
        }

        [Fact]
        public void Catalogo_MarcaFerramentasEPrecoPorMilhao()
        {
            var modelos = CatalogoDoOpenRouter.Ler(
                """{"data":[{"id":"z/sem-tools","name":"Z","context_length":8000,"supported_parameters":["temperature"],"pricing":{"prompt":"0","completion":"0"}},{"id":"a/com-tools","name":"A","context_length":163840,"supported_parameters":["tools","reasoning"],"pricing":{"prompt":"0.00000027","completion":"0.0000011"}}]}""");

            modelos.Select(m => m.Id).Should().Equal("a/com-tools", "z/sem-tools");
            var a = modelos[0];
            a.UsaFerramentas.Should().BeTrue();
            a.Raciocina.Should().BeTrue();
            a.Janela.Should().Be(163840);
            a.EntradaPorMilhao.Should().Be(0.27m);
            modelos[1].UsaFerramentas.Should().BeFalse();
        }

        // ── OpenRouter: roteamento, privacidade, cache, custo, raciocínio, retry ──

        private static ModeloDoOpenRouter Catalogado(string id = "a/b", bool raciocina = false, params string[] parametros) =>
            new(id, id, 100000, true, raciocina, 1m, 2m, new HashSet<string>(parametros));

        [Fact]
        public void ComCatalogo_ExigeParametros_ENaoMandaOQueOModeloNaoAceita()
        {
            var modelo = Catalogado("a/b", raciocina: false, "tools", "max_tokens");
            var corpo = JsonDocument.Parse(Provedor().MontarCorpo(
                new ChatMessage[] { ChatMessage.CreateUserMessage("oi") }, System.Array.Empty<ChatTool>(),
                new ChatRequestOptions(Think: false, NumPredict: 50), stream: true, modelo)).RootElement;

            corpo.GetProperty("provider").GetProperty("require_parameters").GetBoolean().Should().BeTrue();
            corpo.TryGetProperty("reasoning", out _).Should().BeFalse("modelo sem raciocínio + require_parameters recusaria a requisição");
            corpo.TryGetProperty("temperature", out _).Should().BeFalse();
            corpo.GetProperty("max_tokens").GetInt32().Should().Be(50);
        }

        [Fact]
        public void SemCatalogo_NaoExigeParametros_MasPrivacidadeVale()
        {
            var corpo = Corpo(new ChatRequestOptions());
            corpo.GetProperty("provider").TryGetProperty("require_parameters", out _).Should().BeFalse();
            corpo.GetProperty("provider").GetProperty("data_collection").GetString().Should().Be("deny");
            corpo.GetProperty("usage").GetProperty("include").GetBoolean().Should().BeTrue();

            var semPrivacidade = new OpenRouterProvider(new System.Net.Http.HttpClient(), ProvedoresDeIa.UrlDoOpenRouter,
                "k", "a/b", new RegexToolCallHealer(), false, semColetaDeDados: false);
            JsonDocument.Parse(semPrivacidade.MontarCorpo(new ChatMessage[] { ChatMessage.CreateUserMessage("oi") },
                System.Array.Empty<ChatTool>(), new ChatRequestOptions(), true)).RootElement
                .TryGetProperty("provider", out _).Should().BeFalse();
        }

        [Fact]
        public void MarcasDeCache_SoEmAnthropicEGemini_NaAlmaMemoriaEUltimaFala()
        {
            var mensagens = new ChatMessage[]
            {
                ChatMessage.CreateSystemMessage("alma"), ChatMessage.CreateSystemMessage("memória"),
                ChatMessage.CreateUserMessage("antes"), ChatMessage.CreateAssistantMessage("ok"),
                ChatMessage.CreateUserMessage("agora")
            };

            JsonElement Msgs(string modelo) => JsonDocument.Parse(new OpenRouterProvider(new System.Net.Http.HttpClient(),
                ProvedoresDeIa.UrlDoOpenRouter, "k", modelo, new RegexToolCallHealer(), false)
                .MontarCorpo(mensagens, System.Array.Empty<ChatTool>(), new ChatRequestOptions(), true)).RootElement.GetProperty("messages");

            var claude = Msgs("anthropic/claude-sonnet-4");
            int[] marcadas = Enumerable.Range(0, 5).Where(i => claude[i].GetProperty("content").ValueKind == JsonValueKind.Array
                && claude[i].GetProperty("content")[0].TryGetProperty("cache_control", out _)).ToArray();
            marcadas.Should().Equal(0, 1, 4);

            Msgs("deepseek/deepseek-chat").EnumerateArray().Should().NotContain(m => m.GetRawText().Contains("cache_control"),
                "DeepSeek cacheia o prefixo sozinho");
        }

        [Fact]
        public void Trecho_LeCustoEDetalhesDoRaciocinio()
        {
            var t = OpenRouterProvider.LerTrecho(
                """{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","text":"pen","index":0}]}}],"usage":{"prompt_tokens":10,"completion_tokens":2,"cost":0.00042}}""");

            t.Uso!.CustoUsd.Should().Be(0.00042m);
            t.DetalhesDoRaciocinio.Should().ContainSingle();
        }

        [Fact]
        public void DetalhesDoRaciocinio_JuntamTextoPorIndice_EGuardamAssinatura()
        {
            var acumulado = new SortedDictionary<int, System.Text.Json.Nodes.JsonObject>();
            OpenRouterProvider.JuntarDetalhes(acumulado, new[] { (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"type":"reasoning.text","text":"pen","index":0}""")! });
            OpenRouterProvider.JuntarDetalhes(acumulado, new[] { (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"text":"sando","signature":"sig","index":0}""")! });

            acumulado.Should().ContainSingle();
            ((string)acumulado[0]["text"]!).Should().Be("pensando");
            ((string)acumulado[0]["signature"]!).Should().Be("sig");
            ((string)acumulado[0]["type"]!).Should().Be("reasoning.text");
        }

        // ── Com rede fingida ────────────────────────────────────────────────

        private sealed class RedeFingida : System.Net.Http.HttpMessageHandler
        {
            private readonly Func<int, System.Net.Http.HttpResponseMessage> _resposta;
            public List<string> Corpos { get; } = new();
            public RedeFingida(Func<int, System.Net.Http.HttpResponseMessage> resposta) => _resposta = resposta;

            protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage req, CancellationToken ct)
            {
                if (req.Method == System.Net.Http.HttpMethod.Get) return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                Corpos.Add(await req.Content!.ReadAsStringAsync(ct));
                return _resposta(Corpos.Count);
            }
        }

        private static System.Net.Http.HttpResponseMessage Sse(params string[] eventos) => new(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(string.Concat(eventos.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n\n")
        };

        private static OpenRouterProvider ComRede(RedeFingida rede, string modelo = "a/b")
        {
            CatalogoDoOpenRouter.DefinirCache(new[] { Catalogado(modelo, true, "tools", "reasoning", "temperature", "max_tokens") });
            return new OpenRouterProvider(new System.Net.Http.HttpClient(rede), ProvedoresDeIa.UrlDoOpenRouter, "k", modelo,
                new RegexToolCallHealer(), false)
            { EsperasEntreTentativas = new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) } };
        }

        private static async Task<List<StreamChunk>> Drenar(IAsyncEnumerable<StreamChunk> s)
        {
            var l = new List<StreamChunk>();
            await foreach (var c in s) l.Add(c);
            return l;
        }

        [Fact]
        public async Task RaciocinioDaVoltaComFerramenta_VoltaNaIdaSeguinte_DoMesmoTurno()
        {
            var rede = new RedeFingida(n => n == 1
                ? Sse("""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.encrypted","data":"XYZ","index":0}],"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{}"}}]}}]}""")
                : Sse("""{"choices":[{"delta":{"content":"pronto"},"finish_reason":"stop"}]}"""));
            var p = ComRede(rede);
            var ferramenta = ChatTool.CreateFunctionTool("read", "lê", System.BinaryData.FromString("{\"type\":\"object\"}"));

            var historico = new List<ChatMessage> { ChatMessage.CreateUserMessage("lê a.txt") };
            await Drenar(p.StreamAsync(historico, new[] { ferramenta }, new ChatRequestOptions(), CancellationToken.None));

            historico.Add(ChatMessage.CreateAssistantMessage(new[] { ChatToolCall.CreateFunctionToolCall("c1", "read", System.BinaryData.FromString("{}")) }));
            historico.Add(ChatMessage.CreateToolMessage("c1", "conteúdo"));
            await Drenar(p.StreamAsync(historico, new[] { ferramenta }, new ChatRequestOptions(), CancellationToken.None));

            var segunda = JsonDocument.Parse(rede.Corpos[1]).RootElement.GetProperty("messages")[1];
            segunda.GetProperty("reasoning_details")[0].GetProperty("data").GetString().Should().Be("XYZ");

            // Turno seguinte: o raciocínio do turno passado não volta.
            historico.Add(ChatMessage.CreateAssistantMessage("pronto"));
            historico.Add(ChatMessage.CreateUserMessage("e agora?"));
            MensagemSemDetalhes(p, historico).Should().BeTrue();
        }

        private static bool MensagemSemDetalhes(OpenRouterProvider p, List<ChatMessage> historico) =>
            !p.MontarCorpo(historico, System.Array.Empty<ChatTool>(), new ChatRequestOptions(), true).Contains("reasoning_details");

        [Fact]
        public async Task Erro429_TentaDeNovo_E401_Nao()
        {
            var rede = new RedeFingida(n => n == 1
                ? new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)429)
                : Sse("""{"choices":[{"delta":{"content":"oi"}}],"usage":{"prompt_tokens":5,"completion_tokens":1,"cost":0.001}}"""));

            var resultado = await ComRede(rede).CompleteAsync(new ChatMessage[] { ChatMessage.CreateUserMessage("x") },
                System.Array.Empty<ChatTool>(), new ChatRequestOptions(), CancellationToken.None);

            rede.Corpos.Should().HaveCount(2);
            resultado.Text.Should().Be("oi");
            resultado.CustoUsd.Should().Be(0.001m, "a chamada única também traz o custo");

            var recusa = new RedeFingida(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
            Func<Task> chamar = () => ComRede(recusa).CompleteAsync(new ChatMessage[] { ChatMessage.CreateUserMessage("x") },
                System.Array.Empty<ChatTool>(), new ChatRequestOptions(), CancellationToken.None);

            (await chamar.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*chave*");
            recusa.Corpos.Should().HaveCount(1, "chave errada não melhora esperando");
        }

        private sealed class StreamMudo : System.IO.Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => 0; public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return 0; }
            public override long Seek(long o, System.IO.SeekOrigin s) => 0; public override void SetLength(long v) { }
            public override void Write(byte[] b, int o, int c) { }
        }

        [Fact]
        public async Task StreamEmSilencio_EstouraOPrazo_ComFraseLegivel()
        {
            var rede = new RedeFingida(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StreamContent(new StreamMudo()) });
            var p = ComRede(rede);
            p.PrazoDeSilencio = TimeSpan.FromMilliseconds(200);

            Func<Task> ler = () => Drenar(p.StreamAsync(new ChatMessage[] { ChatMessage.CreateUserMessage("x") },
                System.Array.Empty<ChatTool>(), new ChatRequestOptions(), CancellationToken.None));

            (await ler.Should().ThrowAsync<TimeoutException>()).WithMessage("*sem mandar nada*");
        }    }
}
