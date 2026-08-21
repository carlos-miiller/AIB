using System;
using System.Collections.Generic;
using System.Linq;
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
    public class OllamaProviderTests
    {
        private static ChatTool ReadFileTool() => ChatTool.CreateFunctionTool(
            functionName: "read_file",
            functionDescription: "Lê um arquivo.",
            functionParameters: BinaryData.FromString(
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}"));

        /// <summary>Handler que devolve o NDJSON informado e guarda o corpo enviado.</summary>
        private static HttpClient FakeOllama(string ndjson, Action<string> captureBody)
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
                {
                    var body = request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult();
                    if (body != null) captureBody(body);
                    return new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.OK,
                        Content = new StringContent(ndjson)
                    };
                });
            return new HttpClient(handlerMock.Object);
        }

        private static OllamaProvider BuildProvider(HttpClient httpClient) =>
            new OllamaProvider(
                new OllamaNativeClient("http://localhost:11434/v1", httpClient),
                "http://localhost:11434",
                "llama3",
                new RegexToolCallHealer(),
                httpClient,
                verboseLogging: false);

        private static async Task<List<StreamChunk>> DrainAsync(OllamaProvider provider, IReadOnlyList<ChatTool> tools)
        {
            var messages = new List<ChatMessage> { new UserChatMessage("Oi") };
            var chunks = new List<StreamChunk>();
            await foreach (var c in provider.StreamAsync(messages, tools, ChatRequestOptions.Default, CancellationToken.None))
                chunks.Add(c);
            return chunks;
        }

        [Fact]
        public async Task StreamAsync_TwoToolCallsOnTwoNdjsonLines_ProduceTwoDistinctCallKeys()
        {
            // O Ollama reinicia o índice do array tool_calls em 0 a cada linha: indexar por
            // posição de array fundiria as duas chamadas em uma só, corrompida.
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.txt\"}}}]},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":{\"path\":\"b.txt\"}}}]},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, new[] { ReadFileTool() });

            var calls = chunks.OfType<StreamChunk.ToolCallDelta>().ToList();
            calls.Should().HaveCount(2);
            calls.Select(c => c.CallKey).Should().OnlyHaveUniqueItems();
            calls[0].ArgumentsJsonFragment.Should().Contain("a.txt");
            calls[1].ArgumentsJsonFragment.Should().Contain("b.txt");

            chunks.Last().Should().BeOfType<StreamChunk.Done>()
                  .Which.Reason.Should().Be(StreamFinishReason.ToolCalls);
        }

        [Fact]
        public async Task StreamAsync_SendsKeepAliveLockOnEveryChatRequest()
        {
            string? body = null;
            string ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"oi\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, b => body = b));
            await DrainAsync(provider, Array.Empty<ChatTool>());

            body.Should().NotBeNull();
            using var doc = JsonDocument.Parse(body!);
            doc.RootElement.TryGetProperty("keep_alive", out var keepAlive).Should().BeTrue();
            keepAlive.GetInt32().Should().Be(-1);
        }

        [Fact]
        public async Task StreamAsync_NeverHealsAToolCallWrittenInsideAThinkBlock()
        {
            // O raciocínio privado do modelo não pode virar execução real de ferramenta.
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"<think>Action: read_file(a.txt)</think>Ainda não vou ler nada.\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, new[] { ReadFileTool() });

            chunks.OfType<StreamChunk.ToolCallDelta>().Should().BeEmpty();
            chunks.Last().Should().BeOfType<StreamChunk.Done>()
                  .Which.Reason.Should().Be(StreamFinishReason.Stop);
        }

        [Fact]
        public async Task StreamAsync_HealsAToolCallWrittenAsProseInTheFinalChannel()
        {
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"Action: read_file(a.txt)\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, new[] { ReadFileTool() });

            var call = chunks.OfType<StreamChunk.ToolCallDelta>().Should().ContainSingle().Subject;
            call.FunctionName.Should().Be("read_file");
            JsonDocument.Parse(call.ArgumentsJsonFragment!).RootElement
                        .GetProperty("path").GetString().Should().Be("a.txt");

            chunks.Last().Should().BeOfType<StreamChunk.Done>()
                  .Which.Reason.Should().Be(StreamFinishReason.ToolCalls);
        }

        [Fact]
        public async Task StreamAsync_EmitsDoneExactlyOnceAsTheLastChunk()
        {
            string ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"oi\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, Array.Empty<ChatTool>());

            chunks.OfType<StreamChunk.Done>().Should().HaveCount(1);
            chunks.Last().Should().BeOfType<StreamChunk.Done>();
        }

        [Fact]
        public async Task StreamAsync_DoesNotMutateTheMessagesItReceives()
        {
            string ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"oi\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var messages = new List<ChatMessage> { new UserChatMessage("Oi") };

            await foreach (var _ in provider.StreamAsync(messages, Array.Empty<ChatTool>(), ChatRequestOptions.Default, CancellationToken.None))
            {
            }

            messages.Should().HaveCount(1);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Parsing do stream: texto, argumentos degenerados, histórico sujo, cancelamento
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task StreamAsync_TextoChegaEmVariasLinhas_EhConcatenadoNoCanalFinal()
        {
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"Oi, \"},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"tudo \"},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"bem?\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, Array.Empty<ChatTool>());

            string text = string.Concat(chunks
                .OfType<StreamChunk.TextDelta>()
                .Where(d => d.Channel == TextChannel.Final)
                .Select(d => d.Text));

            text.Should().Be("Oi, tudo bem?");
        }

        [Fact]
        public async Task StreamAsync_MarcadorDeThinkPartidoEntreLinhas_NaoVazaParaOUsuario()
        {
            // O splitter guarda o prefixo de marcador incompleto entre chunks; sem isso o
            // usuário via "<thi" solto no balão.
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"<thi\"},\"done\":false}\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"nk>segredo</think>visível\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, Array.Empty<ChatTool>());

            string finalText = string.Concat(chunks
                .OfType<StreamChunk.TextDelta>()
                .Where(d => d.Channel == TextChannel.Final)
                .Select(d => d.Text));

            finalText.Should().Be("visível");
            finalText.Should().NotContain("segredo");
            finalText.Should().NotContain("<thi");
        }

        [Fact]
        public async Task StreamAsync_LinhaDoneTrazContadoresDeUso()
        {
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"oi\"},\"done\":true,\"prompt_eval_count\":42,\"eval_count\":7}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, Array.Empty<ChatTool>());

            var usage = chunks.OfType<StreamChunk.Usage>().Should().ContainSingle().Subject;
            usage.PromptEvalCount.Should().Be(42);
            usage.EvalCount.Should().Be(7);
        }

        [Fact]
        public async Task StreamAsync_ArgumentosComoStringJson_SaoPreservados()
        {
            // Alguns modelos mandam "arguments" como string em vez de objeto.
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, new[] { ReadFileTool() });

            var call = chunks.OfType<StreamChunk.ToolCallDelta>().Should().ContainSingle().Subject;
            call.FunctionName.Should().Be("read_file");
            call.ArgumentsJsonFragment.Should().Contain("a.txt");
        }

        [Fact]
        public async Task StreamAsync_ChamadaSemArgumentos_NaoLanca_ENaoInventaArgumento()
        {
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\"}}]},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, new[] { ReadFileTool() });

            var call = chunks.OfType<StreamChunk.ToolCallDelta>().Should().ContainSingle().Subject;
            call.FunctionName.Should().Be("read_file");
            call.ArgumentsJsonFragment.Should().BeNull();
            chunks.Last().Should().BeOfType<StreamChunk.Done>()
                  .Which.Reason.Should().Be(StreamFinishReason.ToolCalls);
        }

        [Fact]
        public async Task StreamAsync_ArgumentosVaziosNoObjeto_ViramObjetoVazio()
        {
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":{}}}]},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, new[] { ReadFileTool() });

            chunks.OfType<StreamChunk.ToolCallDelta>().Should().ContainSingle()
                  .Which.ArgumentsJsonFragment.Should().Be("{}");
        }

        [Fact]
        public async Task StreamAsync_ToolCallSemNativa_NaoDisparaOCuradorDeRegexAToa()
        {
            // Curador só entra quando NENHUMA tool call nativa apareceu no stream.
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"Action: read_file(b.txt)\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.txt\"}}}]},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, new[] { ReadFileTool() });

            var calls = chunks.OfType<StreamChunk.ToolCallDelta>().ToList();
            calls.Should().HaveCount(1, "a chamada nativa manda; o texto não pode virar uma segunda execução");
            calls[0].ArgumentsJsonFragment.Should().Contain("a.txt");
        }

        [Fact]
        public async Task StreamAsync_SemFerramentasAtivas_NaoCuraProsaComoChamada()
        {
            string ndjson =
                "{\"message\":{\"role\":\"assistant\",\"content\":\"Action: read_file(a.txt)\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, Array.Empty<ChatTool>());

            chunks.OfType<StreamChunk.ToolCallDelta>().Should().BeEmpty();
        }

        [Fact]
        public async Task StreamAsync_TokenCanceladoAntesDeComecar_Lanca()
        {
            string ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"oi\"},\"done\":true}\n";
            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = async () =>
            {
                await foreach (var _ in provider.StreamAsync(
                    new List<ChatMessage> { new UserChatMessage("Oi") },
                    Array.Empty<ChatTool>(),
                    ChatRequestOptions.Default,
                    cts.Token))
                {
                }
            };

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task StreamAsync_CancelamentoNoMeioDoStream_Interrompe()
        {
            var many = new System.Text.StringBuilder();
            for (int i = 0; i < 500; i++)
                many.Append("{\"message\":{\"role\":\"assistant\",\"content\":\"x\"},\"done\":false}\n");
            many.Append("{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n");

            var provider = BuildProvider(FakeOllama(many.ToString(), _ => { }));
            using var cts = new CancellationTokenSource();
            int seen = 0;

            await foreach (var _ in provider.StreamAsync(
                new List<ChatMessage> { new UserChatMessage("Oi") },
                Array.Empty<ChatTool>(),
                ChatRequestOptions.Default,
                cts.Token))
            {
                if (++seen == 5) cts.Cancel();
            }

            // O laço de leitura olha o token a cada linha: parar de ler é o que libera o
            // socket e encerra a geração. Antes o laço testava StreamReader.EndOfStream,
            // uma leitura SÍNCRONA e bloqueante que ignorava o token por completo.
            seen.Should().BeLessThan(50, "o cancelamento tem que cortar o stream, não só marcar a flag");
        }

        [Fact]
        public async Task StreamAsync_HistoricoComArgumentosQuebrados_NaoDerrubaARequisicaoSeguinte()
        {
            // Uma única tool call malformada gravada no histórico fazia TODA requisição
            // seguinte estourar JsonException e inutilizava a sessão inteira.
            string? body = null;
            string ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}\n";
            var provider = BuildProvider(FakeOllama(ndjson, b => body = b));

            var messages = new List<ChatMessage>
            {
                new UserChatMessage("leia o arquivo"),
                ChatMessage.CreateAssistantMessage(new[]
                {
                    ChatToolCall.CreateFunctionToolCall("id0", "read_file", BinaryData.FromString("{\"path\": ")),
                }),
                ChatMessage.CreateToolMessage("id0", "ERRO"),
                new UserChatMessage("e agora?")
            };

            var chunks = new List<StreamChunk>();
            await foreach (var c in provider.StreamAsync(messages, Array.Empty<ChatTool>(), ChatRequestOptions.Default, CancellationToken.None))
                chunks.Add(c);

            chunks.Last().Should().BeOfType<StreamChunk.Done>();

            using var doc = JsonDocument.Parse(body!);
            var assistant = doc.RootElement.GetProperty("messages").EnumerateArray()
                .First(m => m.GetProperty("role").GetString() == "assistant");
            var args = assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments");
            args.ValueKind.Should().Be(JsonValueKind.Object, "JSON quebrado degrada para {} em vez de lançar");
            args.EnumerateObject().Should().BeEmpty();
        }

        [Theory]
        [InlineData("\"texto solto\"")]
        [InlineData("null")]
        [InlineData("123")]
        public async Task StreamAsync_ArgumentosNaoObjetoNoHistorico_DegradamParaObjetoVazio(string rawArguments)
        {
            string? body = null;
            string ndjson = "{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}\n";
            var provider = BuildProvider(FakeOllama(ndjson, b => body = b));

            var messages = new List<ChatMessage>
            {
                ChatMessage.CreateAssistantMessage(new[]
                {
                    ChatToolCall.CreateFunctionToolCall("id0", "read_file", BinaryData.FromString(rawArguments)),
                }),
                ChatMessage.CreateToolMessage("id0", "ok")
            };

            await foreach (var _ in provider.StreamAsync(messages, Array.Empty<ChatTool>(), ChatRequestOptions.Default, CancellationToken.None))
            {
            }

            using var doc = JsonDocument.Parse(body!);
            var assistant = doc.RootElement.GetProperty("messages").EnumerateArray()
                .First(m => m.GetProperty("role").GetString() == "assistant");
            assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments")
                .ValueKind.Should().Be(JsonValueKind.Object);
        }

        [Fact]
        public async Task StreamAsync_LinhasEmBrancoNoNdjson_SaoIgnoradas()
        {
            string ndjson =
                "\n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"oi\"},\"done\":false}\n" +
                "   \n" +
                "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n";

            var provider = BuildProvider(FakeOllama(ndjson, _ => { }));
            var chunks = await DrainAsync(provider, Array.Empty<ChatTool>());

            string text = string.Concat(chunks.OfType<StreamChunk.TextDelta>().Select(d => d.Text));
            text.Should().Be("oi");
            chunks.OfType<StreamChunk.Done>().Should().HaveCount(1);
        }

        [Fact]
        public async Task CompleteAsync_DevolveTextoEContadores()
        {
            string json = "{\"message\":{\"role\":\"assistant\",\"content\":\"SISTEMA ONLINE\"},\"done\":true,\"prompt_eval_count\":11,\"eval_count\":3}";
            var provider = BuildProvider(FakeOllama(json, _ => { }));

            var result = await provider.CompleteAsync(
                new List<ChatMessage> { new UserChatMessage("ping") },
                Array.Empty<ChatTool>(),
                ChatRequestOptions.Default,
                CancellationToken.None);

            result.Text.Should().Be("SISTEMA ONLINE");
            result.PromptEvalCount.Should().Be(11);
            result.EvalCount.Should().Be(3);
        }

        [Fact]
        public async Task WarmupAsync_NuncaLanca_MesmoComOServidorFora()
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("conexão recusada"));

            var httpClient = new HttpClient(handlerMock.Object);
            var provider = BuildProvider(httpClient);

            Func<Task> act = async () => await provider.WarmupAsync(CancellationToken.None);

            await act.Should().NotThrowAsync("o aquecimento é oportunista: falha vira log, nunca crash");
        }

    }
}
