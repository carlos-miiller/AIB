using System;
using AIB.Services;
using AIB.Services.Memory;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O registro de ações do painel — §6.3.
    /// <para>
    /// A regra que estes ensaios protegem é a mesma dos artefatos da memória: o que fica
    /// guardado é o LITERAL. Quem lê esta lista clica nela para abrir o Explorer, ou copia o
    /// comando para rodar de novo — uma paráfrase manda a pessoa para o lugar errado.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class ActionLogServiceTests : IDisposable
    {
        public ActionLogServiceTests() => ActionLogService.Clear();

        public void Dispose() => ActionLogService.Clear();

        private static Artifact Arquivo(string caminho, bool falhou = false) =>
            new(ArtifactKind.FileWritten, "write_file", caminho, falhou);

        [Fact]
        public void MaisRecenteFicaNoTopo()
        {
            ActionLogService.Add(ActionLogService.Construir("write_file", Arquivo(@"C:\a.cs"), false, null, null));
            ActionLogService.Add(ActionLogService.Construir("write_file", Arquivo(@"C:\b.cs"), false, null, null));

            ActionLogService.Entries[0].FullTarget.Should().Be(@"C:\b.cs");
        }

        [Fact]
        public void OLiteralInteiroSobrevive_MesmoComOAlvoEncurtado()
        {
            const string longo = @"C:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\Memory\Compactor.cs";

            ActionLogService.Add(ActionLogService.Construir("write_file", Arquivo(longo), false, null, null));
            var entrada = ActionLogService.Entries[0];

            entrada.FullTarget.Should().Be(longo, "o tooltip mostra o caminho sem abreviação");
            entrada.Target.Length.Should().BeLessThan(longo.Length, "o item da lista mostra encurtado");
        }

        [Fact]
        public void OCorteAcontecePeloComeco_PreservandoOFim()
        {
            // O fim é a parte informativa de um caminho. "…\Memory\Compactor.cs" diz qual
            // arquivo é; "C:\Users\Carlo\CPAPS\AIB\AIBWi…" não diz nada.
            string curto = ActionLogService.Encurtar(
                @"C:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\Memory\Compactor.cs", 30);

            curto.Should().StartWith("…");
            curto.Should().EndWith("Compactor.cs");
        }

        [Fact]
        public void TextoQueCabe_NaoEhTocado()
        {
            ActionLogService.Encurtar("curto.cs", 30).Should().Be("curto.cs");
        }

        [Fact]
        public void FalhaEhRegistradaComAMensagem()
        {
            ActionLogService.Add(ActionLogService.Construir(
                "run_command",
                new Artifact(ArtifactKind.CommandRun, "run_command", "dotnet test", true, "conexão recusada"),
                falhou: true,
                detalhe: "ERRO: 127.0.0.1:11434 recusou a conexão.",
                saidaBruta: null));

            var entrada = ActionLogService.Entries[0];

            entrada.Status.Should().Be(ActionStatus.Failed);
            entrada.Result.Should().Contain("recusou a conexão");
            ActionLogService.Falhas.Should().Be(1);
        }

        [Fact]
        public void FerramentaSemArtefato_AindaEhRegistrada()
        {
            // Sumir com a ação porque não se sabe o literal dela esconderia justamente o que é
            // incomum.
            ActionLogService.Add(ActionLogService.Construir(
                "materialize_skill", artefato: null, falhou: false, detalhe: null, saidaBruta: null));

            ActionLogService.Entries.Should().HaveCount(1);
            ActionLogService.Entries[0].Tool.Should().Be("materialize_skill");
        }

        [Fact]
        public void LeituraEhMarcadaComoSoLeitura()
        {
            // §6.3 pinta o marcador dessas em cinza: leitura não deve competir visualmente com
            // escrita e destruição.
            ActionLogService.Add(ActionLogService.Construir(
                "read_file",
                new Artifact(ArtifactKind.FileRead, "read_file", @"C:\x.md", false),
                false, null, null));

            ActionLogService.Entries[0].SoLeitura.Should().BeTrue();
        }

        [Fact]
        public void LeituraQueFalhou_NaoEhSoLeitura()
        {
            // Falha tem cor própria e precisa vencer o cinza da leitura.
            ActionLogService.Add(ActionLogService.Construir(
                "read_file",
                new Artifact(ArtifactKind.FileRead, "read_file", @"C:\x.md", true),
                falhou: true, detalhe: "ERRO: não encontrado", saidaBruta: null));

            ActionLogService.Entries[0].SoLeitura.Should().BeFalse();
        }

        [Fact]
        public void ComandoVaiParaOCampoDeComando_ArquivoNao()
        {
            ActionLogService.Add(ActionLogService.Construir(
                "run_command",
                new Artifact(ArtifactKind.CommandRun, "run_command", "dotnet build", false),
                false, null, null));

            ActionLogService.Add(ActionLogService.Construir(
                "write_file", Arquivo(@"C:\a.cs"), false, null, null));

            ActionLogService.Entries[1].Command.Should().Be("dotnet build");
            ActionLogService.Entries[0].Command.Should().BeNull("caminho de arquivo não é comando");
        }

        [Fact]
        public void OTetoDoRegistroEhRespeitado()
        {
            for (int i = 0; i < ActionLogService.MaxEntradas + 25; i++)
                ActionLogService.Add(ActionLogService.Construir(
                    "write_file", Arquivo($@"C:\f{i}.cs"), false, null, null));

            ActionLogService.Entries.Should().HaveCount(ActionLogService.MaxEntradas);
            ActionLogService.Entries[0].FullTarget.Should()
                .Be($@"C:\f{ActionLogService.MaxEntradas + 24}.cs");
        }

        [Fact]
        public void RotuloDoDia()
        {
            var hoje = ActionLogService.Construir("write_file", Arquivo(@"C:\a.cs"), false, null, null);
            hoje.DiaRotulo.Should().Be("HOJE");

            var ontem = new ActionLogEntry
            {
                Tool = "write_file",
                Target = "a",
                FullTarget = "a",
                Timestamp = DateTime.Now.AddDays(-1)
            };
            ontem.DiaRotulo.Should().Be("ONTEM");
        }
    }
}
