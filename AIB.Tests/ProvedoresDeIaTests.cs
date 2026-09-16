using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    }
}
