using System;
using System.Collections.Generic;
using System.IO;
using AIB.Services.Memory;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Extração literal de artefatos. Tudo aqui roda sem rede e sem modelo: é justamente o
    /// ponto do extrator ser código puro.
    /// </summary>
    public class ArtifactExtractorTests
    {
        private static ChatMessage ToolCall(string id, string nome, string argsJson) =>
            ChatMessage.CreateAssistantMessage(new[]
            {
                ChatToolCall.CreateFunctionToolCall(id, nome, BinaryData.FromString(argsJson))
            });

        [Fact]
        public void WriteFile_ProduzCaminhoAbsolutoResolvidoETamanho()
        {
            var mensagens = new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("crie o arquivo"),
                ToolCall("c1", "write_file", """{"path":"C:\\temp\\ola.txt","content":"oi"}"""),
                ChatMessage.CreateToolMessage("c1", "SUCESSO: Arquivo salvo corretamente em 'C:\\temp\\ola.txt'."),
                ChatMessage.CreateAssistantMessage("Pronto.")
            };

            var artefatos = ArtifactExtractor.Extract(mensagens);

            artefatos.Should().ContainSingle();
            artefatos[0].Kind.Should().Be(ArtifactKind.FileWritten);
            artefatos[0].Value.Should().Be(@"C:\temp\ola.txt");
            artefatos[0].Failed.Should().BeFalse();
            artefatos[0].Detail.Should().Be("2 caracteres");
        }

        [Fact]
        public void CaminhoRelativo_ViraAbsoluto()
        {
            var mensagens = new List<ChatMessage>
            {
                ToolCall("c1", "read_file", """{"path":"notas.txt"}"""),
                ChatMessage.CreateToolMessage("c1", "conteúdo qualquer")
            };

            var artefatos = ArtifactExtractor.Extract(mensagens);

            // "notas.txt" guardado como está deixaria de ser literal — vira ambiguidade.
            artefatos[0].Value.Should().Be(Path.GetFullPath("notas.txt"));
            artefatos[0].Kind.Should().Be(ArtifactKind.FileRead);
        }

        [Fact]
        public void CaminhoComEscapeQuebrado_EhReparadoAntesDeVirarArtefato()
        {
            // O modelo escreveu \t cru no JSON; o parser leu como TAB e o caminho chegaria
            // como "C:\emp\ola.txt". Registrar isso como fato apontaria para lugar nenhum.
            var mensagens = new List<ChatMessage>
            {
                ToolCall("c1", "write_file", "{\"path\":\"C:\\temp\\\\ola.txt\",\"content\":\"x\"}"),
                ChatMessage.CreateToolMessage("c1", "SUCESSO: Arquivo salvo.")
            };

            ArtifactExtractor.Extract(mensagens)[0].Value.Should().Be(@"C:\temp\ola.txt");
        }

        [Fact]
        public void ComandoQueFalhou_PreservaOErroLiteral()
        {
            var mensagens = new List<ChatMessage>
            {
                ToolCall("c1", "run_command", """{"command":"del C:\\x\\y.txt"}"""),
                ChatMessage.CreateToolMessage("c1", "ERRO: Acesso negado ao caminho.\nlinha irrelevante")
            };

            var artefato = ArtifactExtractor.Extract(mensagens)[0];

            artefato.Kind.Should().Be(ArtifactKind.CommandRun);
            artefato.Failed.Should().BeTrue();
            // "acesso negado" e "arquivo não encontrado" pedem correções opostas.
            artefato.Detail.Should().Be("ERRO: Acesso negado ao caminho.");
        }

        [Fact]
        public void RecusaDoUsuario_VíraArtefatoProprio()
        {
            var mensagens = new List<ChatMessage>
            {
                ToolCall("c1", "write_file", """{"path":"C:\\x.txt","content":"y"}"""),
                ChatMessage.CreateToolMessage("c1", "Ação Rejeitada pelo Usuário.")
            };

            var artefato = ArtifactExtractor.Extract(mensagens)[0];

            artefato.Kind.Should().Be(ArtifactKind.Denied);
            artefato.Failed.Should().BeTrue();
        }

        [Fact]
        public void ChamadaSemResultado_NaoViraArtefato()
        {
            // Turno cortado no meio: houve intenção, não houve execução. Registrar intenção
            // como fato é pior que não registrar nada.
            var mensagens = new List<ChatMessage>
            {
                ToolCall("c1", "write_file", """{"path":"C:\\x.txt","content":"y"}""")
            };

            ArtifactExtractor.Extract(mensagens).Should().BeEmpty();
        }

        [Fact]
        public void ArgumentosMalformados_NaoDerrubamAExtracaoDoTurno()
        {
            var mensagens = new List<ChatMessage>
            {
                ToolCall("c1", "write_file", "{isto não é json"),
                ChatMessage.CreateToolMessage("c1", "ERRO: argumentos inválidos"),
                ToolCall("c2", "read_file", """{"path":"C:\\bom.txt"}"""),
                ChatMessage.CreateToolMessage("c2", "ok")
            };

            var artefatos = ArtifactExtractor.Extract(mensagens);

            artefatos.Should().ContainSingle("a chamada quebrada some, a boa sobrevive");
            artefatos[0].Value.Should().Be(@"C:\bom.txt");
        }

        [Fact]
        public void OrdemDosArtefatos_EhAOrdemDeExecucao()
        {
            var mensagens = new List<ChatMessage>
            {
                ChatMessage.CreateAssistantMessage(new[]
                {
                    ChatToolCall.CreateFunctionToolCall("a", "read_file", BinaryData.FromString("""{"path":"C:\\1.txt"}""")),
                    ChatToolCall.CreateFunctionToolCall("b", "read_file", BinaryData.FromString("""{"path":"C:\\2.txt"}"""))
                }),
                // Resultados chegam fora da ordem em que as chamadas foram declaradas.
                ChatMessage.CreateToolMessage("b", "dois"),
                ChatMessage.CreateToolMessage("a", "um")
            };

            var artefatos = ArtifactExtractor.Extract(mensagens);

            artefatos[0].Value.Should().Be(@"C:\2.txt");
            artefatos[1].Value.Should().Be(@"C:\1.txt");
        }
    }
}
