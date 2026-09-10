using System;
using System.Linq;
using System.Text;
using AIB.Services;
using AIB.Services.Agent;
using AIB.Services.Ai;
using AIB.Services.Mail;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A cadeia entre as iterações de um turno.
    /// <para>
    /// O laço gravava só as <c>tool_calls</c>. A fala que o agente tinha acabado de dizer — "vou
    /// abrir o users.xls" — era mostrada ao usuário e sumia do histórico do próprio modelo. Na
    /// volta seguinte ele via uma chamada e um erro, sem nenhum registro de por que tinha
    /// escolhido aquele caminho. Numa sessão real foram QUATRO chamadas idênticas.
    /// </para>
    /// <para>
    /// Havia um segundo ponto, e sem ele o primeiro não adiantava: o serializador do Ollama
    /// mandava <c>content = ""</c> fixo para toda mensagem com ferramenta, então a fala guardada
    /// nunca chegaria ao modelo de volta.
    /// </para>
    /// </summary>
    public class CadeiaDoTurnoTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // Os padrões, que são a política
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AFalaNoHistorico_NASCE_Ligada()
        {
            // O contrário é defeito, não escolha: a chave existe para desligar se algum modelo
            // reagir mal, não porque desligado seja um estado desejável.
            new UserAppSettings().KeepAssistantSpeech.Should().BeTrue();
        }

        [Fact]
        public void ORaciocinioNoHistorico_ENaTriagem_NASCEM_Desligados()
        {
            // Modelos de raciocínio são treinados esperando o pensamento AUSENTE do histórico.
            // Devolvê-lo vai contra o treino, então é experimento com chave, não padrão.
            var padrao = new UserAppSettings();

            padrao.ThinkingInHistory.Should().BeFalse();
            padrao.MailTriageThinking.Should().BeFalse();
        }

        // ─────────────────────────────────────────────────────────────────────
        // O que entra no histórico
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void SemRaciocinioNoHistorico_OPensamento_EhAPARADO()
        {
            string cru = "<think>O arquivo pode estar na subpasta.</think>Vou abrir o users.xls.";

            string guardado = AgentLoop.ParaOHistorico(cru, new StringBuilder(), false);

            guardado.Should().Be("Vou abrir o users.xls.");
            guardado.Should().NotContain("think");
        }

        [Fact]
        public void ComRaciocinioNoHistorico_OPensamento_VIAJA_Inteiro()
        {
            string cru = "<think>O arquivo pode estar na subpasta.</think>Vou abrir o users.xls.";

            string guardado = AgentLoop.ParaOHistorico(cru, new StringBuilder(), true);

            guardado.Should().Be(cru);
        }

        [Fact]
        public void SemTextoCRU_CaiNoCanalFinal_QueEhOQueOUsuarioLeu()
        {
            // Provider que não publica texto cru: o que sobra é o canal final, exatamente o que
            // apareceu na tela. Nunca uma bolha vazia no histórico.
            var canal = new StringBuilder("Achei o arquivo.");

            AgentLoop.ParaOHistorico(null, canal, false).Should().Be("Achei o arquivo.");
            AgentLoop.ParaOHistorico("", canal, true).Should().Be("Achei o arquivo.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // A mensagem que carrega fala E chamada
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void AMensagemDeFerramenta_CARREGA_AFalaJunto()
        {
            var mensagem = new AssistantChatMessage(new[]
            {
                ChatToolCall.CreateFunctionToolCall("id1", "read", BinaryData.FromString("{}"))
            });

            mensagem.Content.Add(ChatMessageContentPart.CreateTextPart("Vou abrir o users.xls."));

            mensagem.ToolCalls.Should().HaveCount(1);
            mensagem.Content[0].Text.Should().Be("Vou abrir o users.xls.",
                "o protocolo permite texto e tool_calls na mesma mensagem, e é isso que encadeia");
        }

        [Fact]
        public void SemFala_AMensagem_FicaComoAntes()
        {
            var mensagem = new AssistantChatMessage(new[]
            {
                ChatToolCall.CreateFunctionToolCall("id1", "read", BinaryData.FromString("{}"))
            });

            mensagem.Content.Should().BeEmpty("fala vazia não vira parte de conteúdo vazia");
        }

        // ─────────────────────────────────────────────────────────────────────
        // A triagem
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void ATriagem_SemRaciocinio_MANDA_ThinkFalse()
        {
            var opcoes = TriadorDeEmail.Opcoes(comRaciocinio: false);

            opcoes.Think.Should().BeFalse();
            opcoes.NumPredict.Should().Be(TriadorDeEmail.TetoDeResposta);
            opcoes.Temperature.Should().Be(0.0f, "veredito não é lugar de criatividade");
        }

        [Fact]
        public void ATriagem_ComRaciocinio_ALARGA_OTetoDaResposta()
        {
            // O pensamento sai pelo MESMO orçamento de tokens da resposta. Com o teto de sempre,
            // o raciocínio comeria o espaço e o JSON sairia cortado no meio — que a leitura
            // tolerante trataria como "sem veredito", devolvendo uma caixa inteira sem triagem.
            var opcoes = TriadorDeEmail.Opcoes(comRaciocinio: true);

            opcoes.Think.Should().BeNull("ligado é não mandar o campo, e não forçar think:true");
            opcoes.NumPredict.Should().BeGreaterThan(TriadorDeEmail.TetoDeResposta);
        }

        [Fact]
        public void OTriador_ACEITA_ADecisaoDeQuemChama()
        {
            // A opção era estática e privada; agora é de quem chama, porque quem sabe da chave é
            // o vigia, não o triador.
            Action semRaciocinio = () => new TriadorDeEmail(new ProviderQualquer(), false);
            Action comRaciocinio = () => new TriadorDeEmail(new ProviderQualquer(), true);

            semRaciocinio.Should().NotThrow();
            comRaciocinio.Should().NotThrow();

            Action semProvider = () => new TriadorDeEmail(null!, true);
            semProvider.Should().Throw<ArgumentNullException>();
        }

        private sealed class ProviderQualquer : IChatProvider
        {
            public string Name => "x";
            public string Model => "x";

            public async System.Collections.Generic.IAsyncEnumerable<StreamChunk> StreamAsync(
                System.Collections.Generic.IReadOnlyList<ChatMessage> m,
                System.Collections.Generic.IReadOnlyList<ChatTool> t,
                ChatRequestOptions o,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                System.Threading.CancellationToken ct)
            {
                await System.Threading.Tasks.Task.CompletedTask;
                yield break;
            }

            public System.Threading.Tasks.Task<ChatCompletionResult> CompleteAsync(
                System.Collections.Generic.IReadOnlyList<ChatMessage> m,
                System.Collections.Generic.IReadOnlyList<ChatTool> t,
                ChatRequestOptions o, System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.FromResult(new ChatCompletionResult("[]", null, null));

            public System.Threading.Tasks.Task WarmupAsync(System.Threading.CancellationToken ct) =>
                System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
