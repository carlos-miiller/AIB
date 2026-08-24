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
            public ChatRequestOptions? UltimasOpcoes { get; private set; }

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
                UltimasOpcoes = options;

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
        // ─────────────────────────────────────────────────────────────────────
        // Promoção a ato (nível 2)
        // ─────────────────────────────────────────────────────────────────────

        private static Chapter Capitulo(int indice, string resumo, params Artifact[] artefatos) =>
            new(indice, "2026-08-24T00:00:00Z", indice * 2, indice * 2 + 1, resumo, artefatos);

        private static string Texto(ChatMessage mensagem) =>
            mensagem.Content == null
                ? ""
                : string.Concat(mensagem.Content.Where(p => p?.Text != null).Select(p => p.Text));

        [Fact]
        public async Task PromoteAsync_ResumeOsCapitulosNumAtoSo()
        {
            var provider = new DubleProvider("O trecho todo girou em torno de consertar o cache.");
            var compactor = new Compactor(provider);

            var ato = await compactor.PromoteAsync(0, new[]
            {
                Capitulo(0, "primeiro"), Capitulo(1, "segundo"),
                Capitulo(2, "terceiro"), Capitulo(3, "quarto")
            }, CancellationToken.None);

            ato.Index.Should().Be(0);
            ato.FirstChapter.Should().Be(0);
            ato.LastChapter.Should().Be(3);
            ato.Summary.Should().Be("O trecho todo girou em torno de consertar o cache.");
        }

        [Fact]
        public async Task PromoteAsync_HerdaAFaixaDeTurnosDosCapitulos()
        {
            var compactor = new Compactor(new DubleProvider());

            var ato = await compactor.PromoteAsync(0, new[]
            {
                Capitulo(0, "primeiro"), Capitulo(1, "segundo")
            }, CancellationToken.None);

            ato.FirstTurn.Should().Be(0);
            ato.LastTurn.Should().Be(3);
        }

        [Fact]
        public async Task PromoteAsync_NaoMandaFerramentaNenhuma()
        {
            // Mesma razão do resumo de capítulo: um resumidor com ferramenta na mão acaba
            // decidindo "verificar" o que está resumindo, e executa comando no meio da promoção.
            var provider = new DubleProvider();

            await new Compactor(provider).PromoteAsync(0, new[] { Capitulo(0, "x") }, CancellationToken.None);

            provider.UltimasFerramentas.Should().BeEmpty();
            provider.UltimaTemperatura.Should().Be(0f);
        }

        [Fact]
        public async Task PromoteAsync_NaoReenviaArtefatoAoModelo()
        {
            // O literal já sobreviveu ao resumo do capítulo. Passá-lo por um SEGUNDO resumo é
            // como o caminho absoluto vira "um arquivo do provider".
            var provider = new DubleProvider();

            await new Compactor(provider).PromoteAsync(0, new[]
            {
                Capitulo(0, "resumo", new Artifact(ArtifactKind.FileRead, "read_file", @"C:\segredo.cs", false))
            }, CancellationToken.None);

            string enviado = string.Concat(provider.UltimasMensagens.Select(Texto));
            enviado.Should().NotContain(@"C:\segredo.cs");
        }

        [Fact]
        public async Task PromoteAsync_CarregaOsArtefatosCondensadosDosCapitulos()
        {
            var compactor = new Compactor(new DubleProvider());

            var ato = await compactor.PromoteAsync(0, new[]
            {
                Capitulo(0, "a", new Artifact(ArtifactKind.FileRead, "read_file", @"C:.cs", false)),
                Capitulo(1, "b", new Artifact(ArtifactKind.FileWritten, "write_file", @"C:.cs", false))
            }, CancellationToken.None);

            ato.Artifacts.Should().ContainSingle("ler e gravar o mesmo caminho é um artefato só");
            ato.Artifacts[0].Value.Should().Be(@"C:.cs");
        }

        [Fact]
        public async Task PromoteAsync_ModeloForaDoAr_DevolveAtoComNotaEArtefatos()
        {
            // Perder a narrativa é aceitável; devolver nada não é — os capítulos vão sair do
            // prompt de qualquer jeito, e um ato vazio deixaria um buraco silencioso.
            var provider = new DubleProvider(falha: new System.Net.Http.HttpRequestException("conexão recusada"));

            var ato = await new Compactor(provider).PromoteAsync(0, new[]
            {
                Capitulo(0, "a", new Artifact(ArtifactKind.CommandRun, "run_command", "git status", false))
            }, CancellationToken.None);

            ato.Summary.Should().Contain("indisponível");
            ato.Artifacts.Should().ContainSingle();
        }

        [Fact]
        public async Task PromoteAsync_RespostaVazia_NaoVirouAtoMudo()
        {
            var ato = await new Compactor(new DubleProvider("   ")).PromoteAsync(
                0, new[] { Capitulo(0, "a") }, CancellationToken.None);

            ato.Summary.Should().Contain("indisponível");
        }

        [Fact]
        public async Task PromoteAsync_TiraOBlocoDePensamentoDoResumo()
        {
            var provider = new DubleProvider("<think>deixa eu ver...</think>O arco foi sobre o cache.");

            var ato = await new Compactor(provider).PromoteAsync(
                0, new[] { Capitulo(0, "a") }, CancellationToken.None);

            ato.Summary.Should().Be("O arco foi sobre o cache.");
        }

        [Fact]
        public async Task PromoteAsync_SemCapitulo_Recusa()
        {
            var compactor = new Compactor(new DubleProvider());

            await Assert.ThrowsAsync<ArgumentException>(() =>
                compactor.PromoteAsync(0, Array.Empty<Chapter>(), CancellationToken.None));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Custo do resumo
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void MaterialDoResumo_TruncaOTextoDoUsuarioTambem()
        {
            // Log colado no chat, ou arquivo inteiro no corpo da mensagem: sem truncar, isso
            // entrava por completo no prompt do resumidor e estourava o prefill — a mesma
            // espiral de tempo do raciocínio ligado, chegando pelo outro lado.
            var colado = new string('x', 20_000);
            var turno = new Turn(0, new List<ChatMessage>
            {
                new UserChatMessage(colado),
                new AssistantChatMessage("entendi")
            });

            string material = Compactor.RenderForSummary(new[] { turno });

            material.Length.Should().BeLessThan(2_000);
            material.Should().Contain("truncado");
        }

        [Fact]
        public async Task ResumoDeCapitulo_DesligaORaciocinioELimitaASaida()
        {
            // Medido no qwen3.5:4b em CPU: resumir cinco turnos custava 286,6s, dos quais
            // 226,7s eram 1.738 tokens de raciocínio para 117 tokens de resumo — raciocínio que
            // o ThinkBlockStripper descartava logo depois. Com think desligado: 14,7s.
            var provider = new DubleProvider();

            await new Compactor(provider).SummarizeAsync(
                0, TurnosDeExemplo(), CancellationToken.None);

            provider.UltimasOpcoes!.Think.Should().BeFalse();
            provider.UltimasOpcoes!.NumPredict.Should().Be(Compactor.MaxSummaryTokens);
        }

        [Fact]
        public async Task PromocaoDeAto_DesligaORaciocinioTambem()
        {
            var provider = new DubleProvider();

            await new Compactor(provider).PromoteAsync(
                0, new[] { Capitulo(0, "resumo") }, CancellationToken.None);

            provider.UltimasOpcoes!.Think.Should().BeFalse();
            provider.UltimasOpcoes!.NumPredict.Should().Be(Compactor.MaxSummaryTokens);
        }

        [Fact]
        public void RenderChaptersForSummary_MandaSoAsNarrativas()
        {
            string texto = Compactor.RenderChaptersForSummary(new[]
            {
                Capitulo(0, "o usuário pediu A"),
                Capitulo(1, "o usuário pediu B")
            });

            texto.Should().Contain("o usuário pediu A");
            texto.Should().Contain("o usuário pediu B");
            texto.Should().Contain("TRECHO 1");
        }
    }
}
