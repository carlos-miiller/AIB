using System;
using System.Collections.Generic;
using System.Linq;
using AIB.Services.Agent;
using AIB.Services.Memory;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A seção Pendente do bloco de memória. Ela é o que um "continue" retoma quando o turno que
    /// ficou pela metade já virou capítulo — então precisa ser literal e não pode inventar.
    /// </summary>
    public class PendenciasTests
    {
        private static ChatMessage Chamada(string id, string nome, string argsJson) =>
            ChatMessage.CreateAssistantMessage(new[]
            {
                ChatToolCall.CreateFunctionToolCall(id, nome, BinaryData.FromString(argsJson))
            });

        private static IReadOnlyList<Turn> Turnos(params ChatMessage[] mensagens) =>
            TurnSplitter.Split(mensagens);

        private const string Script = """{"path":"C:\\temp\\gerar.ps1","old_string":"a","new_string":"b"}""";

        // ── Extrair ──────────────────────────────────────────────────────────

        [Fact]
        public void EdicaoQueFalhouENaoDeuCertoDepois_EhPendencia()
        {
            var pendencias = Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("ajuste o script"),
                Chamada("c1", "edit", Script),
                ChatMessage.CreateToolMessage("c1", "ERRO: o trecho não existe em 'C:\\temp\\gerar.ps1'."),
                ChatMessage.CreateAssistantMessage("não achei o trecho")));

            var p = pendencias.Should().ContainSingle().Subject;
            p.Tipo.Should().Be(Pendencia.Falha);
            p.Texto.Should().Contain(@"C:\temp\gerar.ps1").And.Contain("o trecho não existe");
        }

        [Fact]
        public void FalhaQueDeuCertoDepois_NaoEhPendencia()
        {
            Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("ajuste o script"),
                Chamada("c1", "edit", Script),
                ChatMessage.CreateToolMessage("c1", "ERRO: o trecho não existe."),
                Chamada("c2", "edit", Script),
                ChatMessage.CreateToolMessage("c2", "SUCESSO: 1 troca(s) em 'C:\\temp\\gerar.ps1'."),
                ChatMessage.CreateAssistantMessage("feito"))).Should().BeEmpty();
        }

        [Fact]
        public void LeituraQueFalhou_ERecusaSua_NaoSaoPendencia()
        {
            // Procurar arquivo que não existe e seguir em frente é comum. E recusa é decisão:
            // listá-la convidaria o modelo a tentar de novo o que você negou.
            Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("veja e apague"),
                Chamada("c1", "read", """{"path":"C:\\temp\\nao-existe.txt"}"""),
                ChatMessage.CreateToolMessage("c1", "ERRO: arquivo não encontrado."),
                Chamada("c2", "shell", """{"command":"del C:\\temp\\x"}"""),
                ChatMessage.CreateToolMessage("c2", "Ação Rejeitada pelo Usuário."),
                ChatMessage.CreateAssistantMessage("ok, não apaguei"))).Should().BeEmpty();
        }

        [Fact]
        public void UltimoTurnoInterrompido_EhPendenciaComPedidoEUltimaAcao()
        {
            var pendencias = Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("crie um script para gerar as assinaturas"),
                Chamada("c1", "shell", """{"command":"dir C:\\temp"}"""),
                ChatMessage.CreateToolMessage("c1", "gerar.ps1"),
                ChatMessage.CreateAssistantMessage(AgentLoop.MarcaDeTurnoMorto("teto de 18 etapas atingido com ferramenta pendente"))));

            var p = pendencias.Should().ContainSingle().Subject;
            p.Tipo.Should().Be(Pendencia.Interrompido);
            p.Texto.Should().Contain("teto de 18 etapas")
                .And.Contain("gerar as assinaturas")
                .And.Contain(@"dir C:\temp");
        }

        [Fact]
        public void TurnoInterrompidoSeguidoDeOutro_NaoEhPendencia()
        {
            Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("faça algo longo"),
                ChatMessage.CreateAssistantMessage(AgentLoop.MarcaDeTurnoMorto("cancelado por você")),
                ChatMessage.CreateUserMessage("deixa para lá"),
                ChatMessage.CreateAssistantMessage("certo"))).Should().BeEmpty();
        }

        // ── Resolver ─────────────────────────────────────────────────────────

        [Fact]
        public void FalhaDeUmTrecho_SomeQuandoUmTrechoPosteriorAcerta()
        {
            var falha = new Pendencia(Pendencia.Falha, "gravar x falhou", @"C:\x.txt", ArtifactKind.FileWritten);
            var acerto = new Artifact(ArtifactKind.FileWritten, "write", @"C:\x.txt", false);

            Pendencias.Resolver(new List<(IReadOnlyList<Pendencia>?, IReadOnlyList<Artifact>)>
            {
                (new[] { falha }, Array.Empty<Artifact>()),
                (null, new[] { acerto })
            }).Should().BeEmpty();
        }

        [Fact]
        public void InterrupcaoEAssunto_SoValemDoTrechoMaisRecente()
        {
            var antiga = new Pendencia(Pendencia.Interrompido, "parou antes");
            var assuntoAntigo = new Pendencia(Pendencia.Assunto, "faltou revisar");
            var recente = new Pendencia(Pendencia.Assunto, "faltou enviar");

            var vivas = Pendencias.Resolver(new List<(IReadOnlyList<Pendencia>?, IReadOnlyList<Artifact>)>
            {
                (new[] { antiga, assuntoAntigo }, Array.Empty<Artifact>()),
                (new[] { recente }, Array.Empty<Artifact>())
            });

            vivas.Should().ContainSingle().Which.Should().Be(recente);
        }

        // ── Resumo do modelo ─────────────────────────────────────────────────

        [Fact]
        public void LinhaPendente_SaiDoParagrafo_EViraItens()
        {
            var (resumo, assunto) = Pendencias.LerDoResumo(
                "O usuário pediu três assinaturas e o agente gerou duas.\nPENDENTE: gerar a assinatura da Gabriely; conferir as imagens.");

            resumo.Should().Be("O usuário pediu três assinaturas e o agente gerou duas.");
            assunto.Select(p => p.Texto).Should().Equal("gerar a assinatura da Gabriely", "conferir as imagens");
            assunto.Should().OnlyContain(p => p.Tipo == Pendencia.Assunto);
        }

        [Theory]
        [InlineData("Tudo feito.\nPENDENTE: nenhuma")]
        [InlineData("Tudo feito.\n**Pendente:** nenhuma.")]
        [InlineData("Tudo feito.")]
        public void SemPendencia_ParagrafoFicaLimpo(string resposta)
        {
            var (resumo, assunto) = Pendencias.LerDoResumo(resposta);

            resumo.Should().Be("Tudo feito.");
            assunto.Should().BeEmpty();
        }

        // ── Comando: assinatura e leitura ────────────────────────────────────

        private static string Shell(string comando) =>
            "{\"command\":" + System.Text.Json.JsonSerializer.Serialize(comando) + "}";

        [Fact]
        public void ComandoQueDeuCertoDepois_ComOutroFIM_NaoEhPendencia()
        {
            // O caso real: `docker compose up` estourou o prazo, a segunda tentativa acrescentou
            // um "| Select-Object -Last 10" e funcionou — e a pendência ficava viva para sempre
            // porque a comparação era pelo texto exato.
            Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("suba o glpi"),
                Chamada("c1", "shell", Shell(@"cd C:\Users\Carlo\GLPI; docker compose up -d --force-recreate glpi")),
                ChatMessage.CreateToolMessage("c1", "ERRO: O comando demorou mais de 30 segundos e foi interrompido."),
                Chamada("c2", "shell", Shell(@"docker compose up -d --force-recreate glpi 2>&1 | Select-Object -Last 10")),
                ChatMessage.CreateToolMessage("c2", "Container glpi Started"),
                ChatMessage.CreateAssistantMessage("subiu"))).Should().BeEmpty();
        }

        [Fact]
        public void ComandoDIFERENTE_QueDeuCerto_NaoApagaAPendenciaAlheia()
        {
            // Chave larga demais seria pior que a estreita: apagaria ponta solta de verdade.
            var pendencias = Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("suba o glpi"),
                Chamada("c1", "shell", Shell("docker compose up -d glpi")),
                ChatMessage.CreateToolMessage("c1", "ERRO (código de saída 1): Volume Created"),
                Chamada("c2", "shell", Shell("docker compose restart db")),
                ChatMessage.CreateToolMessage("c2", "Container db Restarted"),
                ChatMessage.CreateAssistantMessage("reiniciei o banco")));

            pendencias.Should().ContainSingle().Which.Texto.Should().Contain("docker compose up -d glpi");
        }

        [Fact]
        public void ComandoQueSoLE_QuandoFalha_NaoEhPendencia()
        {
            // Procurar e não achar é rotina, e listá-lo convida o modelo a tentar de novo. Eram
            // estes que enchiam a lista: oito `docker exec … grep/cat` que não casaram nada.
            Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("veja a versão"),
                Chamada("c1", "shell", Shell(@"docker exec glpi sh -c ""cat /var/www/glpi/version""")),
                ChatMessage.CreateToolMessage("c1", "ERRO (código de saída 1): No such file"),
                ChatMessage.CreateAssistantMessage("não achei"))).Should().BeEmpty();
        }

        [Theory]
        [InlineData(@"docker exec glpi sed -n '400,435p' /var/www/glpi/src/Plugin.php")]
        [InlineData(@"docker exec glpi sh -c ""grep -n doHook /var/www/glpi/src/Plugin.php""")]
        [InlineData(@"cd C:\Users\Carlo\GLPI; git status")]
        [InlineData(@"Get-Content .\docker-compose.yml | Select-Object -First 5")]
        [InlineData(@"docker compose logs --tail=25 glpi")]
        public void ComandosQueSoOLHAM_SaoReconhecidos(string comando)
        {
            ComandoDeShell.SoLeitura(comando).Should().BeTrue();
        }

        [Theory]
        [InlineData(@"docker exec glpi php /var/www/glpi/plugins/x/test.php")]
        [InlineData(@"New-Item -ItemType Directory -Path C:\temp\x")]
        [InlineData(@"docker compose up -d glpi")]
        [InlineData(@"git push origin main")]
        public void ComandosQueMUDAM_NaoPassamPorLeitura(string comando)
        {
            ComandoDeShell.SoLeitura(comando).Should().BeFalse();
        }

        [Fact]
        public void AAssinatura_IgnoraCanoRedirecionamentoECdNaFrente()
        {
            const string alvo = "docker compose up -d --force-recreate glpi";

            ComandoDeShell.Assinatura(@"cd C:\Users\Carlo\GLPI; docker compose up -d --force-recreate glpi")
                .Should().Be(alvo);
            ComandoDeShell.Assinatura("docker compose up -d --force-recreate glpi 2>&1 | Select-Object -Last 10")
                .Should().Be(alvo);
        }

        // ── No bloco de memória ──────────────────────────────────────────────

        [Fact]
        public void BlocoDeMemoria_TerminaNaSecaoPendente()
        {
            // No fim, colada às mensagens vivas: é o que um "continue" lê primeiro.
            var layer = new MemoryLayer();
            layer.Add(new Chapter(0, "2026-09-16T00:00:00Z", 0, 3, "O agente gerou os arquivos.",
                Array.Empty<Artifact>(),
                Pendencias: new[] { new Pendencia(Pendencia.Interrompido, "o último turno parou sem terminar (teto).") }));

            string bloco = layer.RenderNarrative(new MemoryQuota(0, 0, 5000, 5000), new AIB.Services.TokenCounter());

            bloco.TrimEnd().Should().EndWith("- o último turno parou sem terminar (teto).");
            bloco.Should().Contain("### Pendente");
        }
    }
}
