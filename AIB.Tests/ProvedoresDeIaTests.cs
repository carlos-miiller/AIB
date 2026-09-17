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
    public class ProvedoresDeIaTests : IDisposable
    {
        /// <summary>O catálogo é estático: o fingido destes ensaios não pode sobrar para os outros.</summary>
        public void Dispose() => CatalogoDoOpenRouter.DefinirCache(null);

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

        // ── Limites por provedor ─────────────────────────────────────────────

        [Fact]
        public void Limites_OpenRouterTrazMaisDeUmaVez_OllamaFicaComOsMedidos()
        {
            LimitesDoProvedor.Para(ProvedoresDeIa.Ollama).Should().Be(LimitesDoProvedor.Local);
            LimitesDoProvedor.Para("").Should().Be(LimitesDoProvedor.Local, "vazio é Ollama");
            LimitesDoProvedor.Local.LinhasDeLeitura.Should().Be(AIB.Services.Tools.ReadFileTool.LinhasPadrao);

            var nuvem = LimitesDoProvedor.Para(ProvedoresDeIa.OpenRouter);
            nuvem.LinhasDeLeitura.Should().BeGreaterThan(LimitesDoProvedor.Local.LinhasDeLeitura);
            nuvem.EmailPorLeitura.Should().BeGreaterThan(LimitesDoProvedor.Local.EmailPorLeitura);
            nuvem.LoteDaTriagem.Should().BeGreaterThan(LimitesDoProvedor.Local.LoteDaTriagem);
            nuvem.AlvoDepoisDeCompactar.Should().BeLessThan(LimitesDoProvedor.Local.AlvoDepoisDeCompactar,
                "compactar menos vezes perde o cache menos vezes");
            LimitesDoProvedor.Local.CapitulosPorAto.Should().Be(4, "medido no Ollama; resumo de resumo cedo perde informação");
            nuvem.CapitulosPorAto.Should().Be(8, "cada promoção reescreve o começo do prompt e perde o cache");
        }

        [Fact]
        public void TetoDaRespostaDaTriagem_CresceComOLote()
        {
            AIB.Services.Mail.TriadorDeEmail.Opcoes(false, 25).NumPredict.Should().Be(AIB.Services.Mail.TriadorDeEmail.TetoDeResposta);
            AIB.Services.Mail.TriadorDeEmail.Opcoes(false, 60).NumPredict.Should().Be(AIB.Services.Mail.TriadorDeEmail.TetoDeResposta * 3,
                "60 vereditos não cabem no teto medido para 25");
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

        private static JsonElement MensagensDoCorpo(string modelo, IReadOnlyList<ChatMessage> mensagens) =>
            JsonDocument.Parse(new OpenRouterProvider(new System.Net.Http.HttpClient(),
                ProvedoresDeIa.UrlDoOpenRouter, "k", modelo, new RegexToolCallHealer(), false)
                .MontarCorpo(mensagens, System.Array.Empty<ChatTool>(), new ChatRequestOptions(), true)).RootElement.GetProperty("messages");

        private static int[] Marcadas(JsonElement msgs) => Enumerable.Range(0, msgs.GetArrayLength())
            .Where(i => msgs[i].TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
                        && c.EnumerateArray().Any(p => p.TryGetProperty("cache_control", out _)))
            .ToArray();

        [Fact]
        public void ProvedorFixo_VaiComoOrdemComFallback_EVazioDeixaORoteamentoLivre()
        {
            var fixo = new OpenRouterProvider(new System.Net.Http.HttpClient(), ProvedoresDeIa.UrlDoOpenRouter,
                "k", "a/b", new RegexToolCallHealer(), false, semColetaDeDados: true, provedorFixo: " DeepInfra ");
            var roteamento = JsonDocument.Parse(fixo.MontarCorpo(new ChatMessage[] { ChatMessage.CreateUserMessage("oi") },
                System.Array.Empty<ChatTool>(), new ChatRequestOptions(), true)).RootElement.GetProperty("provider");

            roteamento.GetProperty("order").EnumerateArray().Select(e => e.GetString()).Should().Equal("DeepInfra");
            roteamento.GetProperty("allow_fallbacks").GetBoolean().Should().BeTrue("provedor fora do ar não pode derrubar a conversa");
            roteamento.GetProperty("data_collection").GetString().Should().Be("deny");

            var livre = Corpo(new ChatRequestOptions()).GetProperty("provider");
            livre.TryGetProperty("order", out _).Should().BeFalse();
            livre.TryGetProperty("allow_fallbacks", out _).Should().BeFalse();

            new UserAppSettings().OpenRouterProvedorFixo.Should().BeEmpty("de fábrica o roteamento é livre");
            new UserAppSettings { OpenRouterProvedorFixo = "  Novita " }.Sanear().OpenRouterProvedorFixo.Should().Be("Novita");
        }

        [Fact]
        public void Erro429_DizQualLimite_EQualProvedorRecusou()
        {
            // Resposta real de 17/09 para google/gemma-4-31b-it:free. A frase genérica de antes
            // escondia que a cota era do provedor de baixo, e não da conta.
            OpenRouterProvider.Motivo(
                """{"error":{"message":"Provider returned error","code":429,"metadata":{"raw":"google/gemma-4-31b-it:free is temporarily rate-limited upstream.","provider_name":"Google AI Studio"}}}""")
                .Should().Be("Provider returned error [provedor: Google AI Studio] — google/gemma-4-31b-it:free is temporarily rate-limited upstream.");

            OpenRouterProvider.Motivo("""{"error":{"message":"Rate limit exceeded: free-models-per-day"}}""")
                .Should().Be("Rate limit exceeded: free-models-per-day");
            OpenRouterProvider.Motivo("").Should().Be("sem detalhe");
        }

        [Fact]
        public void ProvedoresDoModelo_LidosDosEndpoints()
        {
            var provedores = CatalogoDoOpenRouter.LerProvedores(
                """
                {"data":{"id":"deepseek/deepseek-v4-flash","endpoints":[
                  {"provider_name":"Novita","quantization":"fp8","pricing":{"prompt":"0.0000004","completion":"0.0000012","input_cache_read":"0.000000026"},"supported_parameters":["tools"],"status":0,"uptime_last_30m":100},
                  {"provider_name":"DeepInfra","quantization":"fp4","pricing":{"prompt":"0.00000006","completion":"0.00000018","input_cache_read":"0.000000015"},"supported_parameters":["tools"],"status":0,"uptime_last_30m":90},
                  {"provider_name":"DeepInfra","quantization":"fp8","pricing":{"prompt":"0.00000006","completion":"0.00000018","input_cache_read":"0.000000015"},"supported_parameters":["tools"],"status":0,"uptime_last_30m":99.8,"throughput_last_30m":45},
                  {"provider_name":"Barato","quantization":"unknown","pricing":{"prompt":"0.00000001","completion":"0.00000002"},"supported_parameters":["temperature"],"status":0}
                ]}}
                """);

            // Quem aceita ferramentas primeiro, e entre eles o mais barato lido do cache. Um nome
            // por provedor, na variante mais disponível.
            provedores.Select(p => p.Nome).Should().Equal("DeepInfra", "Novita", "Barato");
            provedores[0].Quantizacao.Should().Be("fp8");
            provedores[0].Resumo.Should().Contain("cache 0,015").And.Contain("fp8").And.Contain("99,8% no ar").And.Contain("45 tok/s");
            provedores[2].Resumo.Should().Contain("sem cache").And.Contain("SEM ferramentas");
            provedores[0].ToString().Should().Be("DeepInfra", "é o nome que vai para provider.order");

            CatalogoDoOpenRouter.LerProvedores("""{"error":{"message":"not found"}}""").Should().BeEmpty();
        }

        [Fact]
        public void MarcasDeCache_SoEmAnthropicEGemini_NaAlmaMemoriaUltimaFalaEUltimaMensagem()
        {
            var mensagens = new ChatMessage[]
            {
                ChatMessage.CreateSystemMessage("alma"), ChatMessage.CreateSystemMessage("memória"),
                ChatMessage.CreateUserMessage("antes"), ChatMessage.CreateAssistantMessage("ok"),
                ChatMessage.CreateUserMessage("agora")
            };

            Marcadas(MensagensDoCorpo("anthropic/claude-sonnet-4", mensagens)).Should().Equal(0, 1, 4);

            MensagensDoCorpo("deepseek/deepseek-chat", mensagens).EnumerateArray()
                .Should().NotContain(m => m.GetRawText().Contains("cache_control"), "DeepSeek cacheia o prefixo sozinho");
        }

        [Fact]
        public void MarcaDeCache_DentroDoTurnoComFerramenta_VaiNaUltimaMensagem_EmNoMaximoQuatroPontos()
        {
            // Com a marca só na fala do usuário, cada ida de ferramenta relia a preço cheio o que as
            // voltas anteriores do mesmo turno tinham acrescentado.
            var chamada = ChatToolCall.CreateFunctionToolCall("c1", "read", System.BinaryData.FromString("{}"));
            var mensagens = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage("alma"), ChatMessage.CreateSystemMessage("memória"),
                ChatMessage.CreateUserMessage("lê a.txt"),
                ChatMessage.CreateAssistantMessage(new[] { chamada }),
                ChatMessage.CreateToolMessage("c1", "conteúdo do arquivo")
            };

            var msgs = MensagensDoCorpo("anthropic/claude-sonnet-4", mensagens);
            Marcadas(msgs).Should().Equal(0, 1, 2, 4);
            msgs[4].GetProperty("content")[0].GetProperty("text").GetString().Should().Be("conteúdo do arquivo");

            // A mesma mensagem de ferramenta, agora no meio: continua em partes, só sem a marca.
            // Mudar de forma entre requisições arriscaria o prefixo.
            mensagens.Add(ChatMessage.CreateAssistantMessage(new[] { ChatToolCall.CreateFunctionToolCall("c2", "read", System.BinaryData.FromString("{}")) }));
            var depois = MensagensDoCorpo("google/gemini-2.5-pro", mensagens);
            Marcadas(depois).Should().Equal(0, 1, 2, 4).And.HaveCountLessThanOrEqualTo(OpenRouterProvider.TetoDeMarcasDeCache,
                "a última é assistente só com tool_calls: a marca vai na anterior com texto");
            depois[4].GetProperty("content").ValueKind.Should().Be(JsonValueKind.Array);

            mensagens.Add(ChatMessage.CreateToolMessage("c2", "outro"));
            var ultima = MensagensDoCorpo("anthropic/claude-sonnet-4", mensagens);
            Marcadas(ultima).Should().Equal(0, 1, 2, 6);
            ultima[4].GetRawText().Should().Be(depois[4].GetRawText().Replace(",\"cache_control\":{\"type\":\"ephemeral\"}", ""));
        }

        [Fact]
        public void Trecho_LeOProvedorQueAtendeu_EOCache()
        {
            var t = OpenRouterProvider.LerTrecho(
                """{"id":"gen-1","provider":"DeepInfra","model":"deepseek/deepseek-v4-flash-0731","choices":[],"usage":{"prompt_tokens":3465,"completion_tokens":40,"prompt_tokens_details":{"cached_tokens":3100}}}""");

            t.Provedor.Should().Be("DeepInfra");
            t.Uso!.Provedor.Should().Be("DeepInfra");
            t.Uso.CachedTokens.Should().Be(3100);

            OpenRouterProvider.LerTrecho("""{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":1}}""").Uso!
                .CachedTokens.Should().BeNull("sem relato é \"não sei\", e não zero");
        }

        [Fact]
        public async Task OUsoSaiComOProvedor_MesmoQuandoSoOsPrimeirosEventosODizem()
        {
            var rede = new RedeFingida(_ => Sse(
                """{"provider":"Novita","choices":[{"delta":{"content":"oi"}}]}""",
                """{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":0}}}"""));

            var chunks = await Drenar(ComRede(rede).StreamAsync(new ChatMessage[] { ChatMessage.CreateUserMessage("x") },
                System.Array.Empty<ChatTool>(), new ChatRequestOptions(), CancellationToken.None));

            var uso = chunks.OfType<StreamChunk.Usage>().Should().ContainSingle().Subject;
            uso.Provedor.Should().Be("Novita");
            uso.CachedTokens.Should().Be(0, "zero relatado é medida: o cache não pegou");
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
        }

        [Fact]
        public async Task RaciocinioEnviado_ContinuaIdenticoNoTurnoSeguinte_ParaOCacheNaoQuebrar()
        {
            // Antes o raciocínio só ia às voltas do turno corrente: no turno seguinte a mesma
            // mensagem saía sem ele, o prefixo mudava ali e o cache se perdia dali em diante.
            var rede = new RedeFingida(n => n switch
            {
                1 => Sse("""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","text":"vou ler","signature":"s1","index":0}],"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{}"}}]}}]}"""),
                3 => Sse("""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.encrypted","data":"QQ","index":0}],"tool_calls":[{"index":0,"id":"c2","function":{"name":"read","arguments":"{}"}}]}}]}"""),
                _ => Sse("""{"choices":[{"delta":{"content":"pronto"},"finish_reason":"stop"}]}""")
            });
            var p = ComRede(rede);
            var ferramenta = ChatTool.CreateFunctionTool("read", "lê", System.BinaryData.FromString("{\"type\":\"object\"}"));
            async Task Ida(List<ChatMessage> h) => await Drenar(p.StreamAsync(h, new[] { ferramenta }, new ChatRequestOptions(), CancellationToken.None));

            // Turno 1: pede ferramenta, recebe o resultado, responde.
            var historico = new List<ChatMessage> { ChatMessage.CreateSystemMessage("alma"), ChatMessage.CreateUserMessage("lê a.txt") };
            await Ida(historico);
            historico.Add(ChatMessage.CreateAssistantMessage(new[] { ChatToolCall.CreateFunctionToolCall("c1", "read", System.BinaryData.FromString("{}")) }));
            historico.Add(ChatMessage.CreateToolMessage("c1", "conteúdo"));
            await Ida(historico);
            historico.Add(ChatMessage.CreateAssistantMessage("pronto"));

            // Turno 2, com mais uma ida de ferramenta.
            historico.Add(ChatMessage.CreateUserMessage("e o b.txt?"));
            await Ida(historico);
            historico.Add(ChatMessage.CreateAssistantMessage(new[] { ChatToolCall.CreateFunctionToolCall("c2", "read", System.BinaryData.FromString("{}")) }));
            historico.Add(ChatMessage.CreateToolMessage("c2", "outro"));
            await Ida(historico);

            string NoCorpo(int requisicao, int indice) =>
                JsonDocument.Parse(rede.Corpos[requisicao]).RootElement.GetProperty("messages")[indice].GetRawText();

            string noTurno1 = NoCorpo(1, 2);
            noTurno1.Should().Contain("reasoning_details").And.Contain("vou ler");
            NoCorpo(2, 2).Should().Be(noTurno1, "a primeira requisição do turno 2 leva a mensagem igual");
            NoCorpo(3, 2).Should().Be(noTurno1, "e a segunda também");
            NoCorpo(3, 6).Should().Contain("QQ");

            // O prefixo inteiro da última requisição do turno 1 aparece, byte a byte, no turno 2.
            string MensagensCruas(int requisicao) => JsonDocument.Parse(rede.Corpos[requisicao]).RootElement.GetProperty("messages").GetRawText();
            string prefixo = MensagensCruas(1).TrimEnd(']');
            MensagensCruas(3).Should().StartWith(prefixo);
        }

        [Fact]
        public async Task PodaDoRaciocinio_NaoTiraOQueAindaEstaSendoEnviado()
        {
            // Antes o mapa era zerado ao passar de 256, levando junto o raciocínio de mensagens
            // vivas. Agora sai o que foi enviado há mais tempo.
            int n = 0;
            var rede = new RedeFingida(_ =>
            {
                n++;
                return Sse($$$"""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","text":"r{{{n}}}","index":0}],"tool_calls":[{"index":0,"id":"c{{{n}}}","function":{"name":"read","arguments":"{}"}}]}}]}""");
            });
            var p = ComRede(rede);
            var ferramenta = ChatTool.CreateFunctionTool("read", "lê", System.BinaryData.FromString("{\"type\":\"object\"}"));

            var vivo = new List<ChatMessage> { ChatMessage.CreateUserMessage("x") };
            await Drenar(p.StreamAsync(vivo, new[] { ferramenta }, new ChatRequestOptions(), CancellationToken.None));
            vivo.Add(ChatMessage.CreateAssistantMessage(new[] { ChatToolCall.CreateFunctionToolCall("c1", "read", System.BinaryData.FromString("{}")) }));
            vivo.Add(ChatMessage.CreateToolMessage("c1", "ok"));

            for (int i = 0; i < 2100; i++)
                await Drenar(p.StreamAsync(vivo, new[] { ferramenta }, new ChatRequestOptions(), CancellationToken.None));

            p.RaciociniosGuardados.Should().BeLessThanOrEqualTo(2048);
            rede.Corpos[^1].Should().Contain("\"r1\"", "a mensagem c1 foi enviada em toda requisição e nunca envelheceu");
        }

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
