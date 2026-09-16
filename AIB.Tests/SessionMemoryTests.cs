using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AIB.Services.Memory;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Partição em turnos e gravação de raw.jsonl. Escreve sempre em pasta temporária —
    /// nenhum teste toca ~/.AIB do usuário.
    /// </summary>
    public class SessionMemoryTests : IDisposable
    {
        private readonly string _raiz =
            Path.Combine(Path.GetTempPath(), "AIB-testes-memoria", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true); } catch { }
        }

        private static ChatMessage ToolCall(string id, string nome, string argsJson) =>
            ChatMessage.CreateAssistantMessage(new[]
            {
                ChatToolCall.CreateFunctionToolCall(id, nome, BinaryData.FromString(argsJson))
            });

        // ── TurnSplitter ─────────────────────────────────────────────────────

        [Fact]
        public void Split_AgrupaCadaTurnoDoUsuarioAteOProximo()
        {
            var historico = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage("alma"),
                ChatMessage.CreateUserMessage("primeira"),
                ToolCall("c1", "read", """{"path":"C:\\a.txt"}"""),
                ChatMessage.CreateToolMessage("c1", "conteúdo"),
                ChatMessage.CreateAssistantMessage("li o arquivo"),
                ChatMessage.CreateUserMessage("segunda"),
                ChatMessage.CreateAssistantMessage("ok")
            };

            var turnos = TurnSplitter.Split(historico);

            turnos.Should().HaveCount(2);
            turnos[0].Messages.Should().HaveCount(4, "o par tool_call/tool_result fica inteiro dentro do turno");
            turnos[0].UserText.Should().Be("primeira");
            turnos[1].Messages.Should().HaveCount(2);
        }

        [Fact]
        public void Split_NuncaColocaOSystemPromptDentroDeUmTurno()
        {
            var turnos = TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage("alma inteira do personagem"),
                ChatMessage.CreateUserMessage("oi"),
                ChatMessage.CreateAssistantMessage("olá")
            });

            turnos[0].Messages.Should().NotContain(m => m is SystemChatMessage);
        }

        [Fact]
        public void IsClosed_FalsoQuandoOTurnoTerminaComFerramentaPendente()
        {
            var turnos = TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("apague"),
                ToolCall("c1", "shell", """{"command":"del x"}""")
            });

            TurnSplitter.IsClosed(turnos[0]).Should().BeFalse();
        }

        [Fact]
        public void IsClosed_VerdadeiroQuandoOAssistenteDeuAPalavraFinal()
        {
            var turnos = TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("oi"),
                ChatMessage.CreateAssistantMessage("olá")
            });

            TurnSplitter.IsClosed(turnos[0]).Should().BeTrue();
        }

        [Fact]
        public void AssistantText_DescartaOBlocoDeRaciocinio()
        {
            var turnos = TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("2+2?"),
                ChatMessage.CreateAssistantMessage("<think>talvez 5? não, 4.</think>São 4.")
            });

            // Raciocínio é rascunho: contém hipóteses que o próprio modelo descartou.
            turnos[0].AssistantText.Should().Be("São 4.");
        }

        // ── SessionMemory ────────────────────────────────────────────────────

        // ── Turno em curso ───────────────────────────────────────────────────

        private static Turn TurnoInterrompido() => TurnSplitter.Split(new List<ChatMessage>
        {
            ChatMessage.CreateUserMessage("gere as assinaturas"),
            ToolCall("c1", "shell", """{"command":"gerar.ps1"}"""),
            ChatMessage.CreateToolMessage("c1", "Criado: 4 arquivos"),
            ToolCall("c2", "shell", """{"command":"dir"}""")
        })[0];

        [Fact]
        public void TurnoInterrompido_EntraNoRegistroFechado_EOArquivoSome()
        {
            // O AIB caiu no meio de uma cadeia: o raw.jsonl só recebe turno fechado, e o turno
            // inteiro sumia. O arquivo de recuperação o leva ao registro na reabertura.
            var memoria = new SessionMemory("queda", _raiz);
            memoria.GravarTurnoAberto(TurnoInterrompido(), "id-do-turno");

            memoria.RecuperarTurnoAberto("[turno encerrado sem resposta: caiu]").Should().BeTrue();

            var lidos = memoria.ReadTurns();
            lidos.Should().ContainSingle();
            lidos[0].Id.Should().Be("id-do-turno");
            lidos[0].Messages.Select(m => m.Role).Should().Equal("user", "assistant", "tool", "assistant", "assistant");
            lidos[0].Messages[^1].Text.Should().Contain("caiu");
            File.Exists(memoria.TurnoAbertoPath).Should().BeFalse();

            memoria.RecuperarTurnoAberto("marca").Should().BeFalse("não há mais o que recuperar");
            memoria.ReadTurns().Should().ContainSingle();
        }

        [Fact]
        public void TurnoQueJaChegouAoRegistro_NaoEhGravadoDeNovo()
        {
            // A queda pode vir entre gravar o turno e apagar o arquivo de recuperação.
            var memoria = new SessionMemory("queda-tardia", _raiz);
            var turno = TurnoInterrompido();
            memoria.AppendTurn(turno, "mesmo-id").Should().BeTrue();
            memoria.GravarTurnoAberto(turno, "mesmo-id");

            memoria.RecuperarTurnoAberto("marca").Should().BeFalse();

            memoria.ReadTurns().Should().ContainSingle();
            File.Exists(memoria.TurnoAbertoPath).Should().BeFalse();
        }

        [Fact]
        public void TurnoEmCursoIlegivel_FicaOndeEsta()
        {
            var memoria = new SessionMemory("ilegivel", _raiz);
            Directory.CreateDirectory(memoria.SessionDir);
            File.WriteAllText(memoria.TurnoAbertoPath, "{ cortado no mei");

            memoria.RecuperarTurnoAberto("marca").Should().BeFalse();

            File.Exists(memoria.TurnoAbertoPath).Should().BeTrue("apagar seria perder o único rastro do turno");
        }

        [Fact]
        public void AppendTurn_GravaUmaLinhaPorTurnoEReleOQueEscreveu()
        {
            var memoria = new SessionMemory("sessao-teste", _raiz);

            var turnos = TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("crie o arquivo"),
                ToolCall("c1", "write", """{"path":"C:\\temp\\ola.txt","content":"oi"}"""),
                ChatMessage.CreateToolMessage("c1", "SUCESSO: Arquivo salvo."),
                ChatMessage.CreateAssistantMessage("Feito."),
                ChatMessage.CreateUserMessage("obrigado"),
                ChatMessage.CreateAssistantMessage("de nada")
            });

            foreach (var t in turnos) memoria.AppendTurn(t).Should().BeTrue();

            File.ReadAllLines(memoria.RawPath).Should().HaveCount(2);

            var lidos = memoria.ReadTurns();
            lidos.Should().HaveCount(2);
            lidos[0].Messages.Select(m => m.Role).Should().Equal("user", "assistant", "tool", "assistant");
            lidos[0].Messages[1].ToolCalls.Should().ContainSingle().Which.Name.Should().Be("write");
            lidos[0].Messages[2].ToolCallId.Should().Be("c1");
            lidos[0].Artifacts.Should().ContainSingle()
                .Which.Value.Should().Be(@"C:\temp\ola.txt");
        }

        [Fact]
        public void RawJsonl_NaoTemBom()
        {
            var memoria = new SessionMemory("sem-bom", _raiz);
            memoria.AppendTurn(TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("oi"),
                ChatMessage.CreateAssistantMessage("olá")
            })[0]);

            var bytes = File.ReadAllBytes(memoria.RawPath);

            // BOM colado na primeira linha já corrompeu o log de auditoria uma vez.
            bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        }

        [Fact]
        public void Acentos_NaoSaemEscapados()
        {
            var memoria = new SessionMemory("acentos", _raiz);
            memoria.AppendTurn(TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("configuração"),
                ChatMessage.CreateAssistantMessage("ok")
            })[0]);

            // O arquivo é para o usuário abrir e ler.
            File.ReadAllText(memoria.RawPath, Encoding.UTF8).Should().Contain("configuração");
        }

        [Fact]
        public void LinhaCorrompida_EhPuladaSemDerrubarOResto()
        {
            var memoria = new SessionMemory("corrompida", _raiz);
            memoria.AppendTurn(TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("um"),
                ChatMessage.CreateAssistantMessage("1")
            })[0]);

            // Queda de energia no meio de uma escrita deixa exatamente uma linha truncada.
            File.AppendAllText(memoria.RawPath, "{\"Index\":1,\"Mess" + Environment.NewLine);

            memoria.AppendTurn(TurnSplitter.Split(new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("dois"),
                ChatMessage.CreateAssistantMessage("2")
            })[0]);

            memoria.ReadTurns().Should().HaveCount(2, "perder o arquivo inteiro por uma linha seria o pior desfecho");
        }

        [Fact]
        public void SessionId_ComTravessia_NaoEscapaDaPastaDeMemoria()
        {
            var memoria = new SessionMemory(@"..\..\fuga", _raiz);

            memoria.SessionDir.Should().Be(Path.Combine(_raiz, "sessions", "fuga"));
        }

        [Fact]
        public void SessionIdFrom_IncluiMilissegundos()
        {
            var a = SessionMemory.SessionIdFrom(new DateTime(2026, 8, 21, 14, 30, 5, 812));
            var b = SessionMemory.SessionIdFrom(new DateTime(2026, 8, 21, 14, 30, 5, 900));

            a.Should().Be("20260821-143005-812");
            a.Should().NotBe(b, "duas sessões no mesmo segundo não podem escrever na mesma pasta");
        }

        [Fact]
        public void ArquivoInexistente_ReadTurnsDevolveVazioSemLancar()
        {
            new SessionMemory("nunca-escrita", _raiz).ReadTurns().Should().BeEmpty();
        }
    }
}
