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
