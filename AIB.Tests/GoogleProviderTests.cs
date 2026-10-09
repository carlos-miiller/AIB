using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O Google AI Studio como terceiro provedor, pelo endpoint compatível com a OpenAI. Pedido:
    /// usar a chave do AI Studio (a do início rápido, <c>generativelanguage.googleapis.com</c>).
    /// <para>
    /// Tudo com rede fingida. O formato do stream e o da assinatura vêm da documentação do
    /// Google, não de uma resposta vista: o primeiro uso com a API real é que os confirma.
    /// </para>
    /// </summary>
    public class GoogleProviderTests
    {
        private sealed class RedeFingida : HttpMessageHandler
        {
            private readonly Func<int, HttpResponseMessage> _resposta;
            public List<string> Corpos { get; } = new();
            public List<HttpRequestMessage> Pedidos { get; } = new();
            public RedeFingida(Func<int, HttpResponseMessage> resposta) => _resposta = resposta;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                Pedidos.Add(req);
                Corpos.Add(await req.Content!.ReadAsStringAsync(ct));
                return _resposta(Corpos.Count);
            }
        }

        private static HttpResponseMessage Sse(params string[] eventos) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Concat(eventos.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n\n")
        };

        private static HttpResponseMessage Erro(int codigo, string corpo) => new((HttpStatusCode)codigo)
        {
            Content = new StringContent(corpo)
        };

        private static GoogleProvider ComRede(RedeFingida rede, string modelo = "gemini-flash-latest") =>
            new(new HttpClient(rede), ProvedoresDeIa.UrlDoGoogle, "chave-de-ensaio", modelo, new RegexToolCallHealer(), false)
            { EsperasEntreTentativas = new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) } };

        private static async Task<List<StreamChunk>> Drenar(IAsyncEnumerable<StreamChunk> s)
        {
            var l = new List<StreamChunk>();
            await foreach (var c in s) l.Add(c);
            return l;
        }

        private static readonly ChatTool Ler =
            ChatTool.CreateFunctionTool("read", "lê", BinaryData.FromString("{\"type\":\"object\"}"));

        // ── Registro ────────────────────────────────────────────────────────

        [Fact]
        public void OGoogle_EhProvedorDeNuvem_ComChaveNoCofreDele()
        {
            ProvedoresDeIa.Todos.Should().Contain(ProvedoresDeIa.Google);
            ProvedoresDeIa.EhNuvem(ProvedoresDeIa.Google).Should().BeTrue();
            ProvedoresDeIa.EhNuvem(ProvedoresDeIa.Ollama).Should().BeFalse();

            ProvedoresDeIa.SistemaDaChave(ProvedoresDeIa.Google).Should().Be("google");
            ProvedoresDeIa.SistemaDaChave(ProvedoresDeIa.OpenRouter).Should().Be("openrouter", "cada um no seu cofre");
            ProvedoresDeIa.Normalizar("google", null).Should().Be(ProvedoresDeIa.Google);

            var perfil = ProvedoresDeIa.PerfilPadrao(ProvedoresDeIa.Google);
            perfil.Modelo.Should().Be("gemini-flash-latest", "o do início rápido do AI Studio");

            // O endereço não é escolha, e o que a tela mandar de outro provedor não fica.
            perfil.Url = "http://127.0.0.1:11434";
            perfil.KeepAlive = "5m";
            perfil.Sanear(ProvedoresDeIa.Google);
            perfil.Url.Should().Be(ProvedoresDeIa.UrlDoGoogle);
            perfil.KeepAlive.Should().BeEmpty();

            LimitesDoProvedor.Para(ProvedoresDeIa.Google).Should().BeSameAs(LimitesDoProvedor.Nuvem);
        }

        [Theory]
        [InlineData("AIzaSyA-1234567890abcdefghijklmnopqrstuv", true)]
        [InlineData("AQ.Ab8RN6Kx0000000000000000000000000000000000", true)]
        [InlineData("curta", false)]
        [InlineData("com espaço no meio 1234567890123456789012", false)]
        [InlineData("", false)]
        public void AChaveDoGoogle_SoPedeTamanhoECaracteres(string chave, bool vale)
        {
            // Sem exigir o prefixo AIza: o Google já emite chaves em outro formato.
            ProvedoresDeIa.ChaveValida(ProvedoresDeIa.Google, chave).Should().Be(vale);
        }

        [Fact]
        public void AFabrica_ConstroiOGoogle_ComAChaveDele()
        {
            var pedidas = new List<string>();
            var fabrica = new ChatProviderFactory(new HttpClient(), new RegexToolCallHealer(), p => { pedidas.Add(p); return "k"; });

            var provider = fabrica.GetProvider(new UserAppSettings
            {
                AiProvider = ProvedoresDeIa.Google,
                ModelName = "gemini-2.5-flash-lite"
            });

            provider.Should().BeOfType<GoogleProvider>();
            provider.Name.Should().Be(ProvedoresDeIa.Google);
            provider.Model.Should().Be("gemini-2.5-flash-lite");
            pedidas.Should().Equal(ProvedoresDeIa.Google);
        }

        [Fact]
        public async Task OAquecimento_NaoMandaRequisicaoPagaAoGoogle()
        {
            // Achado do estudo: o aquecimento só pulava o OpenRouter, e um terceiro provedor de
            // nuvem receberia o heartbeat — requisição cobrada, para aquecer nada.
            var rede = new RedeFingida(_ => Sse());
            await ComRede(rede).WarmupAsync(CancellationToken.None);
            rede.Corpos.Should().BeEmpty();
        }

        // ── Corpo ───────────────────────────────────────────────────────────

        [Fact]
        public async Task ARequisicao_VaiAoEndpointCompativel_ComAChaveNoBearer()
        {
            var rede = new RedeFingida(_ => Sse("""{"choices":[{"delta":{"content":"oi"},"finish_reason":"stop"}]}"""));
            var chunks = await Drenar(ComRede(rede).StreamAsync(
                new[] { ChatMessage.CreateUserMessage("oi") }, Array.Empty<ChatTool>(), new ChatRequestOptions(), CancellationToken.None));

            var pedido = rede.Pedidos.Single();
            pedido.RequestUri!.ToString().Should().Be("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions");
            pedido.Headers.Authorization!.Scheme.Should().Be("Bearer");
            pedido.Headers.Authorization.Parameter.Should().Be("chave-de-ensaio");

            var corpo = JsonDocument.Parse(rede.Corpos[0]).RootElement;
            corpo.GetProperty("model").GetString().Should().Be("gemini-flash-latest");
            corpo.GetProperty("stream_options").GetProperty("include_usage").GetBoolean().Should().BeTrue();
            corpo.TryGetProperty("provider", out _).Should().BeFalse("roteamento é coisa do OpenRouter");

            chunks.OfType<StreamChunk.TextDelta>().Select(t => t.Text).Should().Equal("oi");
            chunks.OfType<StreamChunk.Done>().Single().Reason.Should().Be(StreamFinishReason.Stop);
        }

        [Theory]
        [InlineData("off", "none")]
        [InlineData("low", "low")]
        [InlineData("high", "high")]
        [InlineData("model", null)]
        public void ORaciocinio_ViraReasoningEffort(string configurado, string? esperado)
        {
            var p = ComRede(new RedeFingida(_ => Sse()));
            var corpo = JsonDocument.Parse(p.MontarCorpo(
                new[] { ChatMessage.CreateUserMessage("oi") }, Array.Empty<ChatTool>(),
                new ChatRequestOptions { Raciocinio = configurado }, stream: true)).RootElement;

            if (esperado == null) corpo.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
            else corpo.GetProperty("reasoning_effort").GetString().Should().Be(esperado);
        }

        [Fact]
        public async Task ModeloQueNaoDesligaORaciocinio_RecebeOPedidoDeNovoSemOCampo()
        {
            // Os Pro recusam reasoning_effort: none com 400. O resumidor e a triagem pedem
            // desligado em toda chamada; sem isto eles nunca funcionariam num Pro.
            var rede = new RedeFingida(n => n == 1
                ? Erro(400, """[{"error":{"code":400,"message":"Thinking can't be disabled for this model.","status":"INVALID_ARGUMENT"}}]""")
                : Sse("""{"choices":[{"delta":{"content":"resumo"},"finish_reason":"stop"}]}"""));
            var p = ComRede(rede, "gemini-2.5-pro");
            var mensagens = new[] { ChatMessage.CreateUserMessage("resuma") };
            var desligado = new ChatRequestOptions { Think = false };

            var resposta = await p.CompleteAsync(mensagens, Array.Empty<ChatTool>(), desligado, CancellationToken.None);

            resposta.Text.Should().Be("resumo");
            rede.Corpos[0].Should().Contain("\"reasoning_effort\":\"none\"");
            rede.Corpos[1].Should().NotContain("reasoning_effort");

            // E lembra: a chamada seguinte já sai sem o campo, sem pagar o 400 de novo.
            await p.CompleteAsync(mensagens, Array.Empty<ChatTool>(), desligado, CancellationToken.None);
            rede.Corpos.Should().HaveCount(3);
            rede.Corpos[2].Should().NotContain("reasoning_effort");
        }

        // ── Ferramentas ─────────────────────────────────────────────────────

        [Fact]
        public async Task AAssinaturaDePensamento_VoltaNaMesmaChamada_EmTodaRequisicaoSeguinte()
        {
            // Sem devolver o extra_content da chamada, os modelos que raciocinam recusam a volta
            // seguinte (400) ou recomeçam a pensar do zero.
            const string chamada = """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"read","arguments":"{\"path\":\"a.txt\"}"},"extra_content":{"google":{"thought_signature":"SIG=="}}}]},"finish_reason":"tool_calls"}]}""";
            var rede = new RedeFingida(n => n == 1
                ? Sse(chamada)
                : Sse("""{"choices":[{"delta":{"content":"pronto"},"finish_reason":"stop"}]}"""));
            var p = ComRede(rede);

            var historico = new List<ChatMessage> { ChatMessage.CreateUserMessage("lê a.txt") };
            var primeira = await Drenar(p.StreamAsync(historico, new[] { Ler }, new ChatRequestOptions(), CancellationToken.None));

            var delta = primeira.OfType<StreamChunk.ToolCallDelta>().Single();
            (delta.ToolCallId, delta.FunctionName, delta.ArgumentsJsonFragment).Should().Be(("c1", "read", "{\"path\":\"a.txt\"}"));
            primeira.OfType<StreamChunk.Done>().Single().Reason.Should().Be(StreamFinishReason.ToolCalls);
            p.ExtrasGuardados.Should().Be(1);

            historico.Add(ChatMessage.CreateAssistantMessage(new[] { ChatToolCall.CreateFunctionToolCall("c1", "read", BinaryData.FromString("{\"path\":\"a.txt\"}")) }));
            historico.Add(ChatMessage.CreateToolMessage("c1", "conteúdo"));
            await Drenar(p.StreamAsync(historico, new[] { Ler }, new ChatRequestOptions(), CancellationToken.None));

            // No turno seguinte também: a mensagem tem de sair igual enquanto estiver no histórico.
            historico.Add(ChatMessage.CreateAssistantMessage("pronto"));
            historico.Add(ChatMessage.CreateUserMessage("e agora?"));
            await Drenar(p.StreamAsync(historico, new[] { Ler }, new ChatRequestOptions(), CancellationToken.None));

            foreach (string corpo in rede.Corpos.Skip(1))
            {
                JsonDocument.Parse(corpo).RootElement.GetProperty("messages")[1].GetProperty("tool_calls")[0]
                    .GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString()
                    .Should().Be("SIG==");
            }
        }

        [Fact]
        public void ChamadasEmParalelo_ComOMesmoIndiceESemId_NaoSeColam()
        {
            // O Gemini manda cada chamada inteira e pode repetir o índice 0 e omitir o id. Pela
            // regra da OpenAI as duas virariam uma só, com os argumentos concatenados.
            var junta = new GoogleProvider.JuntaDeChamadas();
            var deltas = junta.Receber("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"read","arguments":"{\"path\":\"a\"}"}},{"index":0,"function":{"name":"grep","arguments":"{\"q\":\"x\"}"}}]}}]}""");

            deltas.Select(d => d.CallKey).Should().Equal("gg:0", "gg:1");
            deltas.Select(d => d.FunctionName).Should().Equal("read", "grep");
            deltas.Select(d => d.ToolCallId).Should().OnlyContain(id => !string.IsNullOrEmpty(id)).And.OnlyHaveUniqueItems(
                "a resposta da ferramenta precisa de um id para voltar");
        }

        [Fact]
        public void ChamadaEmPedacos_ContinuaAMesma()
        {
            // O formato da OpenAI: o nome e o id no primeiro pedaço, os argumentos aos poucos.
            var junta = new GoogleProvider.JuntaDeChamadas();
            var a = junta.Receber("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read","arguments":"{\"pa"}}]}}]}""");
            var b = junta.Receber("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a\"}"}}]}}]}""");

            a.Concat(b).Select(d => d.CallKey).Should().Equal("gg:0", "gg:0");
            b.Single().ToolCallId.Should().BeNull("o id já foi dito");
            string.Concat(a.Concat(b).Select(d => d.ArgumentsJsonFragment)).Should().Be("{\"path\":\"a\"}");
        }

        // ── Uso e erros ─────────────────────────────────────────────────────

        [Fact]
        public async Task OUso_TrazOCache_ENaoInventaCusto()
        {
            var rede = new RedeFingida(_ => Sse(
                """{"choices":[{"delta":{"content":"oi"},"finish_reason":"stop"}]}""",
                """{"choices":[],"usage":{"prompt_tokens":5000,"completion_tokens":12,"total_tokens":5012,"prompt_tokens_details":{"cached_tokens":4096}}}"""));

            var chunks = await Drenar(ComRede(rede).StreamAsync(
                new[] { ChatMessage.CreateUserMessage("oi") }, Array.Empty<ChatTool>(), new ChatRequestOptions(), CancellationToken.None));

            var uso = chunks.OfType<StreamChunk.Usage>().Single();
            (uso.PromptEvalCount, uso.EvalCount, uso.CachedTokens).Should().Be((5000, 12, 4096));
            uso.CustoUsd.Should().BeNull("o Google não relata o custo; zero seria mentira");
        }

        [Fact]
        public async Task OLimite_EhTentadoDeNovo_EAChaveRecusadaNao()
        {
            var limite = new RedeFingida(n => n < 3
                ? Erro(429, """{"error":{"code":429,"message":"Resource has been exhausted (e.g. check quota).","status":"RESOURCE_EXHAUSTED"}}""")
                : Sse("""{"choices":[{"delta":{"content":"oi"},"finish_reason":"stop"}]}"""));
            await Drenar(ComRede(limite).StreamAsync(
                new[] { ChatMessage.CreateUserMessage("oi") }, Array.Empty<ChatTool>(), new ChatRequestOptions(), CancellationToken.None));
            limite.Corpos.Should().HaveCount(3);

            var chave = new RedeFingida(_ => Erro(400, """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}"""));
            Func<Task> recusada = () => Drenar(ComRede(chave).StreamAsync(
                new[] { ChatMessage.CreateUserMessage("oi") }, Array.Empty<ChatTool>(), new ChatRequestOptions(), CancellationToken.None));

            (await recusada.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*recusou a chave*Configurações*");
            chave.Corpos.Should().ContainSingle("tentar de novo só atrasaria a frase que diz o que fazer");
        }

        [Fact]
        public void OMotivo_SaiDoErro_VenhaComoObjetoOuComoLista()
        {
            GoogleProvider.Motivo("""{"error":{"message":"cota"}}""").Should().Be("cota");
            GoogleProvider.Motivo("""[{"error":{"message":"cota"}}]""").Should().Be("cota");
            GoogleProvider.Motivo("texto solto").Should().Be("texto solto");
            GoogleProvider.Motivo("").Should().Be("sem detalhe");
        }
    }
}
