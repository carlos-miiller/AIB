using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Ai;
using AIB.Services.Memory;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Compactor e MemoryLayer. O provider é dublê: nenhum teste aqui toca a rede.
    /// </summary>
    public class CompactorTests
    {
        private sealed class DubleProvider : IChatProvider
        {
            private readonly string _resposta;
            private readonly Exception? _falha;

            public DubleProvider(string resposta = "O usuário pediu, o agente fez, deu certo.", Exception? falha = null)
            {
                _resposta = resposta;
                _falha = falha;
            }

            public string Name => "Duble";
            public string Model => "duble";

            public List<ChatMessage> UltimasMensagens { get; } = new();
            public List<ChatTool> UltimasFerramentas { get; } = new();
            public float UltimaTemperatura { get; private set; } = -1;

            public IAsyncEnumerable<StreamChunk> StreamAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                CancellationToken ct) => Vazio();

            private static async IAsyncEnumerable<StreamChunk> Vazio()
            {
                await Task.Yield();
                yield break;
            }

            public Task<ChatCompletionResult> CompleteAsync(
                IReadOnlyList<ChatMessage> messages,
                IReadOnlyList<ChatTool> tools,
                ChatRequestOptions options,
                CancellationToken ct)
            {
                UltimasMensagens.Clear();
                UltimasMensagens.AddRange(messages);
                UltimasFerramentas.Clear();
                UltimasFerramentas.AddRange(tools);
                UltimaTemperatura = options.Temperature;

                if (_falha != null) throw _falha;
                return Task.FromResult(new ChatCompletionResult(_resposta, null, null));
            }

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private static ChatMessage ToolCall(string id, string nome, string argsJson) =>
            ChatMessage.CreateAssistantMessage(new[]
            {
                ChatToolCall.CreateFunctionToolCall(id, nome, BinaryData.FromString(argsJson))
            });

        private static IReadOnlyList<Turn> TurnosDeExemplo() =>
            TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("crie um arquivo de notas"),
                ToolCall("c1", "write_file", """{"path":"C:\\temp\\notas.txt","content":"conteúdo"}"""),
                ChatMessage.CreateToolMessage("c1", "SUCESSO: Arquivo salvo."),
                ChatMessage.CreateAssistantMessage("Criei o arquivo."),
                ChatMessage.CreateUserMessage("agora apague"),
                ToolCall("c2", "run_command", """{"command":"del C:\\temp\\notas.txt"}"""),
                ChatMessage.CreateToolMessage("c2", "ERRO: Acesso negado."),
                ChatMessage.CreateAssistantMessage("Não consegui, acesso negado.")
            });

        [Fact]
        public async Task Capitulo_JuntaResumoNarrativoEArtefatosLiterais()
        {
            var provider = new DubleProvider();
            var capitulo = await new Compactor(provider).SummarizeAsync(0, TurnosDeExemplo(), default);

            capitulo.Summary.Should().Be("O usuário pediu, o agente fez, deu certo.");
            capitulo.Artifacts.Should().HaveCount(2);
            capitulo.Artifacts[0].Value.Should().Be(@"C:\temp\notas.txt");
            capitulo.Artifacts[1].Failed.Should().BeTrue("o que falhou vale tanto quanto o que deu certo");
            capitulo.FirstTurn.Should().Be(0);
            capitulo.LastTurn.Should().Be(1);
        }

        [Fact]
        public async Task Resumidor_NuncaRecebeFerramentas()
        {
            var provider = new DubleProvider();
            await new Compactor(provider).SummarizeAsync(0, TurnosDeExemplo(), default);

            // Resumir não é agir: com ferramentas na mão o resumidor acaba "verificando" o
            // que resume e executando comando no meio da compactação.
            provider.UltimasFerramentas.Should().BeEmpty();
            provider.UltimaTemperatura.Should().Be(0f);
        }

        [Fact]
        public async Task Resumidor_NaoRecebeAAlmaDoPersonagem()
        {
            var provider = new DubleProvider();
            await new Compactor(provider).SummarizeAsync(0, TurnosDeExemplo(), default);

            provider.UltimasMensagens.Should().HaveCount(2, "prompt do resumidor + o material a resumir");
            provider.UltimasMensagens[0].Should().BeOfType<SystemChatMessage>();
        }

        [Fact]
        public async Task ModeloForaDoAr_AindaProduzCapituloComOsArtefatos()
        {
            var provider = new DubleProvider(falha: new InvalidOperationException("conexão recusada"));

            var capitulo = await new Compactor(provider).SummarizeAsync(0, TurnosDeExemplo(), default);

            // Perder a narrativa é aceitável; devolver null deixaria um buraco silencioso no
            // lugar de turnos que o chamador vai remover do contexto.
            capitulo.Summary.Should().Contain("indisponível");
            capitulo.Artifacts.Should().HaveCount(2, "os literais não dependem do modelo");
        }

        [Fact]
        public async Task RespostaVazia_ViraNotaEmVezDeResumoEmBranco()
        {
            var capitulo = await new Compactor(new DubleProvider("   ")).SummarizeAsync(0, TurnosDeExemplo(), default);

            capitulo.Summary.Should().Contain("indisponível");
        }

        [Fact]
        public async Task BlocoDeRaciocinio_NaoEntraNoResumo()
        {
            var provider = new DubleProvider("<think>vou resumir assim</think>O agente criou o arquivo.");

            var capitulo = await new Compactor(provider).SummarizeAsync(0, TurnosDeExemplo(), default);

            capitulo.Summary.Should().Be("O agente criou o arquivo.");
        }

        [Fact]
        public async Task Cancelamento_Propaga_NaoViraCapituloMutilado()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var provider = new DubleProvider(falha: new OperationCanceledException());

            var acao = async () => await new Compactor(provider).SummarizeAsync(0, TurnosDeExemplo(), cts.Token);

            await acao.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public void MaterialDoResumo_TruncaResultadoDeFerramentaEOmiteOsLiterais()
        {
            var turnos = TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("leia o arquivo"),
                ToolCall("c1", "read_file", """{"path":"C:\\grande.txt"}"""),
                ChatMessage.CreateToolMessage("c1", new string('x', 5000)),
                ChatMessage.CreateAssistantMessage("li")
            });

            string material = Compactor.RenderForSummary(turnos);

            // O conteúdo inteiro de um arquivo lido não ajuda a resumir e é justamente o que
            // estouraria o contexto do resumidor.
            material.Length.Should().BeLessThan(1000);
            material.Should().Contain("AGENTE CHAMOU: read_file");
            material.Should().Contain("truncado");
        }

        [Fact]
        public void SemTurnos_LancaEmVezDeProduzirCapituloVazio()
        {
            var acao = () => new Compactor(new DubleProvider()).SummarizeAsync(0, Array.Empty<Turn>(), default);

            acao.Should().ThrowAsync<ArgumentException>();
        }

        // ── MemoryLayer ──────────────────────────────────────────────────────

        private static Chapter Cap(int i, string resumo) =>
            new(i, "2026-08-21T00:00:00Z", i * 2, i * 2 + 1, resumo, Array.Empty<Artifact>());

        [Fact]
        public void Render_SaiEmOrdemCronologicaMesmoEscolhendoDeTrasParaFrente()
        {
            var layer = new MemoryLayer();
            layer.AddRange(new[] { Cap(0, "primeiro"), Cap(1, "segundo"), Cap(2, "terceiro") });

            string bloco = layer.Render(new MemoryQuota(0, 0, 5000, 5000), new TokenCounter());

            bloco.IndexOf("primeiro", StringComparison.Ordinal)
                .Should().BeLessThan(bloco.IndexOf("terceiro", StringComparison.Ordinal),
                    "capítulos fora de ordem fariam o modelo ler a conversa de trás para frente");
        }

        [Fact]
        public void CotaApertada_SacrificaOPassadoDistanteEGuardaORecente()
        {
            var layer = new MemoryLayer();
            var counter = new TokenCounter();
            layer.AddRange(new[]
            {
                Cap(0, new string('a', 400) + " antigo"),
                Cap(1, new string('b', 400) + " recente")
            });

            int cabeUmSo = counter.CountText(layer.Chapters[1].Render()) + 5;
            string bloco = layer.Render(new MemoryQuota(0, 0, cabeUmSo, 5000), counter);

            bloco.Should().Contain("recente");
            bloco.Should().NotContain("antigo");
        }

        [Fact]
        public void MemoriaDesligada_NaoRenderizaNada()
        {
            var layer = new MemoryLayer();
            layer.Add(Cap(0, "qualquer coisa"));

            layer.Render(new MemoryQuota(0, 0, 0, 1500), new TokenCounter()).Should().BeEmpty();
        }

        [Fact]
        public void SemCapitulos_NaoRenderizaCabecalhoOrfao()
        {
            new MemoryLayer().Render(new MemoryQuota(0, 0, 5000, 5000), new TokenCounter())
                .Should().BeEmpty();
        }

        [Fact]
        public void NextChapterIndex_ContinuaDeOndeParou()
        {
            var layer = new MemoryLayer();
            layer.NextChapterIndex.Should().Be(0);

            layer.Add(Cap(0, "x"));
            layer.Add(Cap(1, "y"));

            layer.NextChapterIndex.Should().Be(2);
            layer.LastCoveredTurn.Should().Be(3);
        }

        [Fact]
        public void RenderDeCapitulo_MostraArtefatosComoLiteraisUsaveis()
        {
            var capitulo = new Chapter(0, "2026-08-21T00:00:00Z", 0, 1, "resumo qualquer", new[]
            {
                new Artifact(ArtifactKind.FileWritten, "write_file", @"C:\temp\a.txt", false, "12 caracteres"),
                new Artifact(ArtifactKind.CommandRun, "run_command", "del x", true, "ERRO: negado")
            });

            string texto = capitulo.Render();

            texto.Should().Contain(@"C:\temp\a.txt");
            texto.Should().Contain("[FALHOU]");
            texto.Should().Contain("ERRO: negado");
        }
    }
}
