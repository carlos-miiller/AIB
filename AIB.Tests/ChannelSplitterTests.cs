using System.Collections.Generic;
using System.Linq;
using AIB.Services.Ai;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    public class ChannelSplitterTests
    {
        private static string Final(IEnumerable<StreamChunk.TextDelta> deltas) =>
            string.Concat(deltas.Where(d => d.Channel == TextChannel.Final).Select(d => d.Text));

        private static string Reasoning(IEnumerable<StreamChunk.TextDelta> deltas) =>
            string.Concat(deltas.Where(d => d.Channel == TextChannel.Reasoning).Select(d => d.Text));

        [Fact]
        public void Push_SeparatesThinkFromFinalAnswer()
        {
            var splitter = new ChannelSplitter();

            var deltas = splitter.Push("<think>raciocínio interno</think>Resposta ao usuário.").ToList();
            deltas.AddRange(splitter.Flush(anyToolCallSeen: false));

            Final(deltas).Should().Be("Resposta ao usuário.");
            Reasoning(deltas).Should().Contain("raciocínio interno");
            splitter.FinalText.Should().Be("Resposta ao usuário.");
            splitter.AnythingEmittedAsFinal.Should().BeTrue();
        }

        [Fact]
        public void TurnoQueSoPensou_PeloCampoSeparado_EmiteOPensamentoComoResposta()
        {
            // Defeito real: o modelo raciocinou, chegou à resposta DENTRO do pensamento e
            // encerrou sem emitir nada no canal final e sem chamar ferramenta. O usuário viu um
            // balão vazio, com o caminho do arquivo que ele procurava preso no raciocínio.
            //
            // O fallback existia, mas só para a tag inline <think> nunca fechada. O campo
            // separado message.thinking — que é como qwen3.5 opera — não passava por ele.
            var splitter = new ChannelSplitter();

            var deltas = new List<StreamChunk.TextDelta>();
            deltas.AddRange(splitter.PushThinking("Encontrei o arquivo. "));
            deltas.AddRange(splitter.PushThinking(@"C:\Users\Carlo\Downloads\Ramais.xlsx"));
            deltas.AddRange(splitter.Flush(anyToolCallSeen: false));

            Final(deltas).Should().Contain("Ramais.xlsx", "melhor vazar o pensamento que devolver vazio");
            splitter.AnythingEmittedAsFinal.Should().BeTrue();
        }

        [Fact]
        public void TurnoQuePensouERespondeu_NaoRepeteOPensamento()
        {
            // O fallback é último recurso. Havendo resposta de verdade, o pensamento fica onde
            // sempre esteve: no canal de raciocínio e no histórico, nunca no balão.
            var splitter = new ChannelSplitter();

            var deltas = new List<StreamChunk.TextDelta>();
            deltas.AddRange(splitter.PushThinking("vou procurar em Downloads"));
            deltas.AddRange(splitter.Push("Achei em Downloads."));
            deltas.AddRange(splitter.Flush(anyToolCallSeen: false));

            Final(deltas).Should().Be("Achei em Downloads.");
            Final(deltas).Should().NotContain("vou procurar");
        }

        [Fact]
        public void TurnoQuePensouEChamouFerramenta_NaoVazaOPensamento()
        {
            // Turno que termina em ferramenta não tem resposta para dar ainda: o texto viria
            // na iteração seguinte. Vazar o pensamento aqui encheria a conversa de rascunho a
            // cada passo de uma tarefa de vários passos.
            var splitter = new ChannelSplitter();

            var deltas = new List<StreamChunk.TextDelta>();
            deltas.AddRange(splitter.PushThinking("preciso listar a pasta primeiro"));
            deltas.AddRange(splitter.Flush(anyToolCallSeen: true));

            Final(deltas).Should().BeEmpty();
            splitter.AnythingEmittedAsFinal.Should().BeFalse();
        }

        [Fact]
        public void Push_ReassemblesMarkerSplitAcrossChunks()
        {
            var splitter = new ChannelSplitter();

            var deltas = new List<StreamChunk.TextDelta>();
            deltas.AddRange(splitter.Push("<th"));
            deltas.AddRange(splitter.Push("ink>segredo</thi"));
            deltas.AddRange(splitter.Push("nk>Olá."));
            deltas.AddRange(splitter.Flush(anyToolCallSeen: false));

            // O marker partido nunca pode vazar como resposta.
            Final(deltas).Should().Be("Olá.");
            Final(deltas).Should().NotContain("segredo");
            Reasoning(deltas).Should().Contain("segredo");
        }

        [Fact]
        public void Flush_LeaksUnclosedThinkAsAnswerWhenNothingElseWasEmitted()
        {
            var splitter = new ChannelSplitter();

            var deltas = splitter.Push("<think>a resposta estava presa aqui").ToList();
            Final(deltas).Should().BeEmpty();

            deltas.AddRange(splitter.Flush(anyToolCallSeen: false));

            Final(deltas).Should().Be("a resposta estava presa aqui");
        }

        [Fact]
        public void Flush_DoesNotLeakUnclosedThinkWhenAToolCallHappened()
        {
            var splitter = new ChannelSplitter();

            var deltas = splitter.Push("<think>pensando em chamar ferramenta").ToList();
            deltas.AddRange(splitter.Flush(anyToolCallSeen: true));

            Final(deltas).Should().BeEmpty();
            splitter.AnythingEmittedAsFinal.Should().BeFalse();
        }

        [Fact]
        public void Push_StripsChatTemplateTokensFromFinalChannel()
        {
            var splitter = new ChannelSplitter();

            var deltas = splitter.Push("Oi<|im_end|><|eot_id|>").ToList();
            deltas.AddRange(splitter.Flush(anyToolCallSeen: false));

            Final(deltas).Should().Be("Oi");
        }

        [Fact]
        public void Push_NeverEmitsEmptyDeltas()
        {
            var splitter = new ChannelSplitter();

            var deltas = splitter.Push("<think></think>").ToList();
            deltas.AddRange(splitter.Flush(anyToolCallSeen: false));

            deltas.Should().OnlyContain(d => d.Text.Length > 0);
        }

        [Fact]
        public void RawText_PreservaOStreamCruComOsBlocosDeThink()
        {
            // É o RawText que vai para o histórico: o modelo relê o próprio raciocínio.
            var splitter = new ChannelSplitter();
            splitter.Push("<think>vou somar</think>");
            splitter.Push("São 4.");
            splitter.Flush(anyToolCallSeen: false);

            splitter.RawText.Should().Be("<think>vou somar</think>São 4.");
            splitter.FinalText.Should().Be("São 4.");
        }
    
        // ── Raciocinio pelo campo separado (message.thinking) ────────────────

        [Fact]
        public void PushThinking_FechaOBlocoQuandoOContentComeca()
        {
            // REGRESSAO: o </think> era anexado so no Flush, entao a resposta final entrava
            // DENTRO do bloco de raciocinio. Medido com qwen3.5:9b, o historico gravou
            // "<think>...raciocinio...Kai online. Ola.</think>" e no turno seguinte o modelo
            // reeleu o proprio turno sem encontrar resposta alguma — e repetiu a assinatura.
            var s = new ChannelSplitter();
            s.PushThinking("raciocinando");
            s.Push("resposta final");
            s.Flush(anyToolCallSeen: false);

            s.RawText.Should().Be("<think>raciocinando</think>resposta final");
        }

        [Fact]
        public void PushThinking_SemContent_FechaNoFlush()
        {
            var s = new ChannelSplitter();
            s.PushThinking("so pensei");
            s.Flush(anyToolCallSeen: true);

            s.RawText.Should().Be("<think>so pensei</think>",
                "turno que so raciocinou nao pode deixar o bloco pendurado");
        }

        [Fact]
        public void PushThinking_VaiParaOCanalReasoning_NuncaParaOUsuario()
        {
            var s = new ChannelSplitter();
            var deltas = s.PushThinking("interno");

            deltas.Should().ContainSingle();
            deltas[0].Channel.Should().Be(TextChannel.Reasoning);
            s.FinalText.Should().BeEmpty("raciocinio nunca vira texto do usuario");
        }

        [Fact]
        public void PushThinking_AbreOBlocoUmaVezSo()
        {
            var s = new ChannelSplitter();
            s.PushThinking("um ");
            s.PushThinking("dois");
            s.Flush(anyToolCallSeen: true);

            s.RawText.Should().Be("<think>um dois</think>");
        }
}
}
