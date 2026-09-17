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
        public void CorrenteQueFalhou_CasaComOPassoQueDeuCertoDepois()
        {
            // A falha é relatada para a corrente inteira. O caso real: `cd X; docker compose up …;
            // Start-Sleep …; docker compose logs …` estourou o prazo, o `docker compose up`
            // sozinho funcionou logo depois, e a pendência sobreviveu à conversa inteira.
            Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("suba o glpi"),
                Chamada("c1", "shell", Shell(@"cd C:\Users\Carlo\GLPI; docker compose up -d --force-recreate glpi; Start-Sleep -Seconds 20; docker compose logs --tail=25 glpi")),
                ChatMessage.CreateToolMessage("c1", "ERRO: O comando demorou mais de 30 segundos e foi interrompido."),
                Chamada("c2", "shell", Shell("docker compose up -d --force-recreate glpi")),
                ChatMessage.CreateToolMessage("c2", "Container glpi Started"),
                ChatMessage.CreateAssistantMessage("subiu"))).Should().BeEmpty();
        }

        [Fact]
        public void FalhaQueVoltouADAR_ERRADO_EntraUMAVezSo()
        {
            // Falhou, deu certo, falhou de novo: uma ponta solta, não duas. A chave entrava na
            // ordem uma segunda vez e a linha saía repetida no bloco.
            var pendencias = Pendencias.Extrair(Turnos(
                ChatMessage.CreateUserMessage("rode o teste"),
                Chamada("c1", "shell", Shell("docker exec glpi php teste.php")),
                ChatMessage.CreateToolMessage("c1", "ERRO (código de saída 1): falhou"),
                Chamada("c2", "shell", Shell("docker exec glpi php teste.php")),
                ChatMessage.CreateToolMessage("c2", "tudo certo"),
                Chamada("c3", "shell", Shell("docker exec glpi php teste.php")),
                ChatMessage.CreateToolMessage("c3", "ERRO (código de saída 1): falhou de novo"),
                ChatMessage.CreateAssistantMessage("voltou a falhar")));

            pendencias.Should().ContainSingle().Which.Texto.Should().Contain("falhou de novo");
        }

        [Fact]
        public void OAssuntoQueREPETE_UmaFalha_NaoEntraDeNovo()
        {
            // O resumidor do ato lê o Pendente dos capítulos e às vezes devolve as mesmas falhas
            // na linha PENDENTE: dele. A mesma ponta solta por duas bocas continua sendo uma.
            const string comando = "docker exec glpi php /var/www/glpi/plugins/x/test.php";

            var falha = new Pendencia(Pendencia.Falha,
                $"executar {comando} 2>&1 | Select-Object -Last 20 falhou e não deu certo depois: ERRO",
                comando + " 2>&1 | Select-Object -Last 20", ArtifactKind.CommandRun);

            var assunto = new Pendencia(Pendencia.Assunto,
                $"executar {comando} falhou e não deu certo depois");

            var outro = new Pendencia(Pendencia.Assunto, "limpar os dados de teste no banco");

            var vivas = Pendencias.Resolver(new[]
            {
                ((IReadOnlyList<Pendencia>?)new[] { falha, assunto, outro },
                 (IReadOnlyList<Artifact>)Array.Empty<Artifact>())
            });

            vivas.Should().HaveCount(2);
            vivas.Should().Contain(p => p.Tipo == Pendencia.Falha);
            vivas.Should().Contain(p => p.Texto == "limpar os dados de teste no banco");
        }

        [Fact]
        public void AAssinatura_IgnoraCanoRedirecionamentoECdNaFrente()
        {
            const string alvo = "docker compose up -d --force-recreate glpi";

            ComandoDeShell.Assinatura(@"cd C:\Users\Carlo\GLPI; docker compose up -d --force-recreate glpi")
                .Should().Be(alvo);
            ComandoDeShell.Assinatura("docker compose up -d --force-recreate glpi 2>&1 | Select-Object -Last 10")
                .Should().Be(alvo);
            ComandoDeShell.Assinatura(@"cd C:\x; docker compose up -d --force-recreate glpi; Start-Sleep -Seconds 20; docker compose logs glpi")
                .Should().Be(alvo, "numa corrente, o trabalho é o primeiro passo que não é preparo");
        }

        [Fact]
        public void AAssinatura_NaoCorta_NoPontoEVirgula_DE_DENTRO_DeUmArgumento()
        {
            // Ponto e vírgula dentro de aspas é texto, não corrente: cortar ali inventaria um
            // comando que ninguém rodou.
            // A aspa do fim cai junto com a pontuação de borda; o que importa é o "; " ter ficado.
            ComandoDeShell.Assinatura(@"Set-Content C:\a.txt -Value ""linha 1; linha 2""")
                .Should().StartWith(@"set-content c:\a.txt -value ""linha 1; linha 2");
        }

        [Fact]
        public void AAssinatura_DentroDoShC_PegaOPrimeiroPasso()
        {
            // Depois de tirar o embrulho, `sh -c "a; b"` é uma corrente como outra qualquer — e
            // vale a mesma regra: o trabalho é o primeiro passo.
            ComandoDeShell.Assinatura(@"docker exec glpi sh -c ""php bin/console plugin:install x; php bin/console plugin:activate x""")
                .Should().Be("php bin/console plugin:install x");
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
