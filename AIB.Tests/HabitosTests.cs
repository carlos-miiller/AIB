using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Os hábitos que o personagem pega na convivência. Pedido do usuário em 15/10/2026: "existe
    /// alguma forma de Ellen adquirir hábitos, costumes ou formas de agir baseado nas interações?
    /// por exemplo se ela faz algo e eu reajo positivamente a isso, de forma orgânica".
    /// </summary>
    public class HabitosTests : IDisposable
    {
        private readonly string _raiz = Path.Combine(Path.GetTempPath(), "AIB_TestHabitos_" + Guid.NewGuid().ToString("N"));
        private static readonly DateTime Agora = new(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);
        private static readonly Atributos Neutro = new();

        public void Dispose()
        {
            try { Directory.Delete(_raiz, recursive: true); } catch { }
        }

        private static Reacao Gostou(string jeito) => new(true, null, jeito, "kkkk");
        private static Reacao NaoGostou(string jeito) => new(false, null, jeito, "para com isso");

        // ── A consolidação ─────────────────────────────────────────────

        [Fact]
        public void UmaReacao_NaoViraRegra_VistaDeNovo_Entra()
        {
            // Uma reação mal lida pelo resumidor não pode virar hábito de primeira.
            var h = new Habitos(_raiz);
            var vazio = Array.Empty<Habito>();

            h.Aprender("Ellen", new[] { Gostou("faz trocadilhos com esgrima") }, vazio, Neutro, Agora);
            h.De("Ellen").Should().ContainSingle().Which.Forca.Should().Be(1.0, "a curiosidade neutra nasce em 1");
            h.Ativos("Ellen", Agora, 0.10).Should().BeEmpty("visto uma vez só");

            var conhecidos = h.De("Ellen");
            h.Aprender("Ellen", new[] { new Reacao(true, 1, "", "de novo isso haha") }, conhecidos, Neutro, Agora.AddDays(2));

            var ativo = h.Ativos("Ellen", Agora.AddDays(2), 0.10).Should().ContainSingle().Subject;
            ativo.Vezes.Should().Be(2);
            ativo.Forca.Should().BeGreaterThan(Habitos.Limiar);
        }

        [Fact]
        public void JeitoDitoDeOutroModo_ReforcaOGuardado_NaoNasceOutro()
        {
            // Caso real (22/10/2026): o arquivo da Ellen tinha "demonstra carinho e humor na
            // conversa" e "demonstra carinho e humor ao narrar", cada um com a própria força. O
            // resumidor escrevia o jeito de novo em vez do número, e só o texto idêntico casava.
            var h = new Habitos(_raiz);
            h.Aprender("Ellen", new[] { Gostou("demonstra carinho e humor na conversa") }, Array.Empty<Habito>(), Neutro, Agora);
            h.Aprender("Ellen", new[] { Gostou("demonstra carinho e humor ao narrar") }, Array.Empty<Habito>(), Neutro, Agora);

            var unico = h.De("Ellen").Should().ContainSingle().Subject;
            unico.Texto.Should().Be("demonstra carinho e humor na conversa");
            unico.Vezes.Should().Be(2);
        }

        [Fact]
        public void JeitosDiferentes_ContinuamSeparados()
        {
            Habitos.Semelhanca("usa humor e entusiasmo na conversa", "demonstra carinho e humor na conversa")
                .Should().BeLessThan(Habitos.Parecido);
            Habitos.Semelhanca("manda mensagens longas", "manda mensagens curtas")
                .Should().BeLessThan(Habitos.Parecido);
            Habitos.Semelhanca("pede desculpa ao interromper", "pede para continuar sem reclamar do erro")
                .Should().BeLessThan(Habitos.Parecido);
        }

        [Theory]
        [InlineData("chama o agente pelo nome")]
        [InlineData("chama a agente pelo nome, \"ellen\"")]
        [InlineData("elogia a Ellen com entusiasmo")]
        public void JeitoQueFalaDoAgente_EDoUsuario_NaoEntra(string jeito)
        {
            // Caso real (22/10/2026): sete dos doze hábitos da Ellen eram "chama a agente pelo
            // nome..." — o que o USUÁRIO fazia. Viravam "o usuário gosta quando você chama o
            // agente pelo nome" no prompt.
            var h = new Habitos(_raiz);
            h.Aprender("Ellen", new[] { Gostou(jeito) }, Array.Empty<Habito>(), Neutro, Agora);

            h.De("Ellen").Should().BeEmpty();
        }

        [Fact]
        public void ArquivoComRepetidos_SeArrumaAoSerLido()
        {
            // Caso real (22/10/2026): o arquivo gravado antes da comparação por semelhança. Ao ler,
            // os do usuário saem e os parecidos se fundem no mais forte.
            string pasta = Path.Combine(_raiz, "memory", ConversaDoOrbe.Sessao, "habitos");
            Directory.CreateDirectory(pasta);
            Habito H(string t, double f, int v) => new() { Texto = t, Forca = f, Vezes = v, VistoUtc = Agora };
            var gravados = new List<Habito>
            {
                H("chama o agente pelo nome", 2.25, 2),
                H("chama a agente pelo nome, \"ellen\"", 5, 15),
                H("demonstra carinho e humor ao narrar", 2.25, 2),
                H("demonstra carinho e humor na conversa", 4.14, 4),
                H("usa humor e entusiasmo na conversa", 5, 5),
                H("pede desculpa ao interromper", 5, 5),
            };
            ArquivoCifrado.Gravar(Path.Combine(pasta, "Ellen.dat"), System.Text.Json.JsonSerializer.Serialize(gravados));

            var h = new Habitos(_raiz);
            h.De("Ellen").Select(x => x.Texto).Should().BeEquivalentTo(
                "demonstra carinho e humor na conversa", "usa humor e entusiasmo na conversa", "pede desculpa ao interromper");
            new Habitos(_raiz).De("Ellen").Should().HaveCount(3, "o arrumado foi regravado");
        }

        [Fact]
        public void ACuriosidade_DaAForcaComQueOJeitoNasce()
        {
            Habitos.Nasce(1).Should().Be(0.5);
            Habitos.Nasce(5).Should().Be(1.5);
            Habitos.Nasce(0).Should().Be(1.0, "zero não é nível: vale o neutro");
        }

        [Fact]
        public void ReacaoContraria_DerrubaOJeito()
        {
            var h = new Habitos(_raiz);
            h.Aprender("Ellen", new[] { Gostou("manda bom-dia") }, Array.Empty<Habito>(), Neutro, Agora);
            h.Aprender("Ellen", new[] { NaoGostou("manda bom-dia") }, Array.Empty<Habito>(), Neutro, Agora);

            h.De("Ellen").Should().BeEmpty("1 - 2 fica abaixo do mínimo e o jeito some");
        }

        [Fact]
        public void OQueEleNaoGosta_TambemViraHabito()
        {
            var h = new Habitos(_raiz);
            for (int i = 0; i < 2; i++)
                h.Aprender("Ellen", new[] { NaoGostou("manda mensagens longas") }, Array.Empty<Habito>(), Neutro, Agora);

            var habito = h.Ativos("Ellen", Agora, 0.10).Should().ContainSingle().Subject;
            habito.Evita.Should().BeTrue();
            Habitos.Linha(new[] { habito }).Should().Contain("não gosta quando você manda mensagens longas");
        }

        [Fact]
        public void SemSerVisto_EsqueceDevagar_PelaConstancia()
        {
            var h = new Habito { Forca = 2, VistoUtc = Agora };

            Habitos.ForcaEm(h, Agora.AddDays(7), 0.10).Should().BeApproximately(1.8, 1e-9, "10% por SEMANA");
            Habitos.ForcaEm(h, Agora.AddDays(7), 0.05).Should().BeApproximately(1.9, 1e-9, "constância alta esquece menos");
        }

        [Fact]
        public void ONumero_ApontaPeloTexto_ENumeroQueNaoExisteEIgnorado()
        {
            // O arquivo pode mudar enquanto o resumo roda: o número vale pelo texto da lista
            // que o resumidor recebeu, e não pela posição no arquivo de agora.
            var h = new Habitos(_raiz);
            h.Aprender("Ellen", new[] { Gostou("faz trocadilhos"), Gostou("chama ele de chefe") },
                       Array.Empty<Habito>(), Neutro, Agora);
            var conhecidos = h.De("Ellen");
            h.Esquecer("Ellen", "faz trocadilhos");

            h.Aprender("Ellen", new[] { new Reacao(true, 2, "", "x"), new Reacao(true, 9, "", "x") },
                       conhecidos, Neutro, Agora);

            h.De("Ellen").Should().ContainSingle().Which.Vezes.Should().Be(2);
        }

        [Fact]
        public void OsHabitos_SaoDeCadaPersonagem_ECifrados()
        {
            var h = new Habitos(_raiz);
            h.Aprender("Ellen", new[] { Gostou("faz trocadilhos com esgrima") }, Array.Empty<Habito>(), Neutro, Agora);

            h.De("Kai").Should().BeEmpty();
            string arquivo = Directory.GetFiles(_raiz, "Ellen.dat", SearchOption.AllDirectories).Should().ContainSingle().Subject;
            File.ReadAllText(arquivo).Should().NotContain("esgrima", "o texto cita a conversa e vai cifrado");
        }

        [Fact]
        public void SemHabitoFirme_ALinhaNaoExiste()
        {
            // O prompt de quem ainda não pegou nenhum fica byte a byte o que era.
            Habitos.Linha(Array.Empty<Habito>()).Should().BeEmpty();
            new UserAppSettings().AprenderHabitos.Should().BeTrue("nasce ligada, pedido do usuário");
        }

        // ── O resumidor ────────────────────────────────────────────────

        private static Turn Turno(string usuario, string agente, string? ferramenta = null)
        {
            var mensagens = new List<ChatMessage> { new UserChatMessage(usuario) };
            if (ferramenta != null)
            {
                mensagens.Add(ChatMessage.CreateAssistantMessage(new[]
                {
                    ChatToolCall.CreateFunctionToolCall("c1", "read", BinaryData.FromString("{}"))
                }));
                mensagens.Add(new ToolChatMessage("c1", ferramenta));
            }
            mensagens.Add(new AssistantChatMessage(agente));
            return new Turn(0, mensagens);
        }

        [Fact]
        public void OJeito_SoValeComAFalaDoUsuario()
        {
            // A fala citada é a prova. Tirada de um arquivo lido (conteúdo de terceiros) ou
            // inventada, a reação não passa.
            var turnos = new[] { Turno("KKKK adorei, Ellen!", "En garde!", ferramenta: "o usuário disse: amo textos longos") };
            string resposta = "OBJETIVO: conversar.\nAPRENDIDO: nenhum\nPENDENTE: nenhuma\nJEITO:\n"
                            + "- + faz trocadilhos com esgrima | \"kkkk adorei\"\n"
                            + "- + manda textos longos | \"amo textos longos\"\n"
                            + "- - chama ele de chefe | \"nunca disse isso\"";

            var (resto, reacoes) = Compactor.LerJeito(resposta, turnos);

            reacoes.Should().ContainSingle().Which.Should().Be(new Reacao(true, null, "faz trocadilhos com esgrima", "kkkk adorei"));
            resto.Should().NotContain("JEITO").And.NotContain("trocadilhos");
        }

        [Fact]
        public void ALinhaDoJeito_NaoViraLicao()
        {
            // As linhas do JEITO são "- ..." como as lições; sem cortar a seção antes, elas
            // entrariam no APRENDIDO do capítulo.
            var turnos = new[] { Turno("haha boa", "Touché!") };
            string resposta = "OBJETIVO: conversar.\nAPRENDIDO:\n- o backup roda às 3h\nPENDENTE: nenhuma\nJEITO:\n- + #1 | \"haha boa\"";

            var (resto, reacoes) = Compactor.LerJeito(resposta, turnos);
            var (_, aprendido, _) = Compactor.LerSecoes(resto, "o backup roda às 3h");

            reacoes.Should().ContainSingle().Which.Numero.Should().Be(1);
            aprendido.Should().Equal("o backup roda às 3h");
        }

        private sealed class Resumidor : IChatProvider
        {
            public string Resposta = "OBJETIVO: conversar.\nAPRENDIDO: nenhum\nPENDENTE: nenhuma";
            public List<ChatMessage> Mensagens { get; } = new();
            public string Name => "Duble";
            public string Model => "duble";

            public IAsyncEnumerable<StreamChunk> StreamAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatTool> t,
                                                             ChatRequestOptions o, CancellationToken ct) =>
                throw new NotSupportedException();

            public Task<ChatCompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatTool> t,
                                                            ChatRequestOptions o, CancellationToken ct)
            {
                Mensagens.Clear();
                Mensagens.AddRange(m);
                return Task.FromResult(new ChatCompletionResult(Resposta, null, null));
            }

            public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;
        }

        private static string Texto(ChatMessage m) => string.Concat(m.Content.Select(p => p.Text));

        [Fact]
        public async Task SemPersona_OPromptDoCapituloNaoMuda()
        {
            var resumidor = new Resumidor();
            await new Compactor(resumidor).SummarizeAsync(0, new[] { Turno("oi", "olá") }, CancellationToken.None);

            Texto(resumidor.Mensagens[0]).Should().NotContain("JEITO");
            Texto(resumidor.Mensagens[1]).Should().NotContain("JEITOS JÁ CONHECIDOS");
        }

        [Fact]
        public async Task ComPersona_PerguntaPeloJeito_ComOsConhecidosNumerados()
        {
            var resumidor = new Resumidor
            {
                Resposta = "OBJETIVO: conversar.\nAPRENDIDO: nenhum\nPENDENTE: nenhuma\nJEITO:\n- + #1 | \"haha boa\""
            };
            var conhecidos = new[] { new Habito { Texto = "faz trocadilhos com esgrima" } };
            var compactor = new Compactor(resumidor, jeitos: conhecidos);

            var capitulo = await compactor.SummarizeAsync(0, new[] { Turno("haha boa", "Touché!") }, CancellationToken.None);

            Texto(resumidor.Mensagens[0]).Should().EndWith(Compactor.SecaoDoJeito.TrimEnd());
            Texto(resumidor.Mensagens[1]).Should().Contain("#1 + faz trocadilhos com esgrima");
            compactor.Reacoes.Should().ContainSingle().Which.Numero.Should().Be(1);
            capitulo.Objetivo.Should().Be("conversar.");
        }
    }
}
