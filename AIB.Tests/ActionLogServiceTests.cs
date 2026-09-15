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
            new(ArtifactKind.FileWritten, "write", caminho, falhou);

        [Fact]
        public void MaisRecenteFicaNoTopo()
        {
            ActionLogService.Add(ActionLogService.Construir("write", Arquivo(@"C:\a.cs"), false, null, null));
            ActionLogService.Add(ActionLogService.Construir("write", Arquivo(@"C:\b.cs"), false, null, null));

            ActionLogService.Entries[0].FullTarget.Should().Be(@"C:\b.cs");
        }

        [Fact]
        public void OLiteralInteiroSobrevive_MesmoComOAlvoEncurtado()
        {
            const string longo = @"C:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\Memory\Compactor.cs";

            ActionLogService.Add(ActionLogService.Construir("write", Arquivo(longo), false, null, null));
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
                "shell",
                new Artifact(ArtifactKind.CommandRun, "shell", "dotnet test", true, "conexão recusada"),
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
                "read",
                new Artifact(ArtifactKind.FileRead, "read", @"C:\x.md", false),
                false, null, null));

            ActionLogService.Entries[0].SoLeitura.Should().BeTrue();
        }

        [Fact]
        public void LeituraQueFalhou_NaoEhSoLeitura()
        {
            // Falha tem cor própria e precisa vencer o cinza da leitura.
            ActionLogService.Add(ActionLogService.Construir(
                "read",
                new Artifact(ArtifactKind.FileRead, "read", @"C:\x.md", true),
                falhou: true, detalhe: "ERRO: não encontrado", saidaBruta: null));

            ActionLogService.Entries[0].SoLeitura.Should().BeFalse();
        }

        [Fact]
        public void ComandoVaiParaOCampoDeComando_ArquivoNao()
        {
            ActionLogService.Add(ActionLogService.Construir(
                "shell",
                new Artifact(ArtifactKind.CommandRun, "shell", "dotnet build", false),
                false, null, null));

            ActionLogService.Add(ActionLogService.Construir(
                "write", Arquivo(@"C:\a.cs"), false, null, null));

            ActionLogService.Entries[1].Command.Should().Be("dotnet build");
            ActionLogService.Entries[0].Command.Should().BeNull("caminho de arquivo não é comando");
        }

        [Fact]
        public void OTetoDoRegistroEhRespeitado()
        {
            for (int i = 0; i < ActionLogService.MaxEntradas + 25; i++)
                ActionLogService.Add(ActionLogService.Construir(
                    "write", Arquivo($@"C:\f{i}.cs"), false, null, null));

            ActionLogService.Entries.Should().HaveCount(ActionLogService.MaxEntradas);
            ActionLogService.Entries[0].FullTarget.Should()
                .Be($@"C:\f{ActionLogService.MaxEntradas + 24}.cs");
        }

        private static TurnRecord Turno(int indice, DateTime utc, params MessageRecord[] mensagens) =>
            new(indice, utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                mensagens, Array.Empty<Artifact>());

        private static MessageRecord Chamadas(params ToolCallRecord[] chamadas) =>
            new("assistant", "", chamadas);

        private static MessageRecord Resultado(string id, string texto) =>
            new("tool", texto, null, id);

        [Fact]
        public void ReabrirConversa_RemontaAsAcoesDoRaw_ComAlvoResumoEHora()
        {
            // O defeito: o registro vive só em memória. Reabrir uma conversa mostrava a aba de
            // ações vazia, embora cada chamada e cada resultado estivessem no raw.jsonl.
            var quando = new DateTime(2026, 9, 15, 13, 5, 0, DateTimeKind.Utc);

            var turno = Turno(0, quando,
                new MessageRecord("user", "troque o nome e procure os html"),
                Chamadas(
                    new ToolCallRecord("c1", "edit",
                        """{"path":"C:\\temp\\a.html","old_string":"Ana","new_string":"Bia"}"""),
                    new ToolCallRecord("c2", "glob", """{"pattern":"*.html","path":"C:\\temp"}""")),
                Resultado("c1", @"SUCESSO: 1 troca(s) em 'C:\temp\a.html'."),
                Resultado("c2", @"ERRO: a pasta 'C:\temp' não existe."),
                new MessageRecord("assistant", "pronto"));

            var acoes = ActionLogService.Reconstruir(new[] { turno });

            acoes.Should().HaveCount(2);

            acoes[0].Tool.Should().Be("edit");
            acoes[0].FullTarget.Should().Be(@"C:\temp\a.html");
            acoes[0].Result.Should().Be("+1 linha, −1 linha");
            acoes[0].Timestamp.Should().Be(quando.ToLocalTime(), "a hora é a gravada, não a da reabertura");

            acoes[1].Status.Should().Be(ActionStatus.Failed);
            acoes[1].FullTarget.Should().Be(@"*.html em C:\temp");
            acoes[1].Result.Should().Contain("não existe");
        }

        [Fact]
        public void ReabrirConversa_ChamadaSemResultado_NaoViraAcao()
        {
            // Turno cortado no meio: houve intenção, não houve execução.
            var turno = Turno(0, DateTime.UtcNow,
                Chamadas(new ToolCallRecord("c1", "write", """{"path":"C:\\x.txt","content":"y"}""")));

            ActionLogService.Reconstruir(new[] { turno }).Should().BeEmpty();
        }

        [Fact]
        public void Restaurar_TrocaORegistroInteiro_MaisRecenteNoTopo_ComUmAvisoSo()
        {
            ActionLogService.Add(ActionLogService.Construir("write", Arquivo(@"C:\de-outra-conversa.cs"), false, null, null));

            var turnos = new[]
            {
                Turno(0, DateTime.UtcNow.AddMinutes(-5),
                    Chamadas(new ToolCallRecord("a", "read", """{"path":"C:\\primeiro.md"}""")),
                    Resultado("a", "     1\tolá")),
                Turno(1, DateTime.UtcNow,
                    Chamadas(new ToolCallRecord("b", "read", """{"path":"C:\\segundo.md"}""")),
                    Resultado("b", "     1\tmundo"))
            };

            int avisos = 0;
            void Contar(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => avisos++;
            ActionLogService.Entries.CollectionChanged += Contar;

            try
            {
                ActionLogService.Restaurar(ActionLogService.Reconstruir(turnos));
            }
            finally
            {
                ActionLogService.Entries.CollectionChanged -= Contar;
            }

            ActionLogService.Entries.Should().HaveCount(2, "a ação da outra conversa sai");
            ActionLogService.Entries[0].FullTarget.Should().Be(@"C:\segundo.md");
            avisos.Should().Be(1, "o painel remonta a aba a cada aviso — um só, e não um por entrada");
        }

        [Fact]
        public void RotuloDoDia()
        {
            var hoje = ActionLogService.Construir("write", Arquivo(@"C:\a.cs"), false, null, null);
            hoje.DiaRotulo.Should().Be("HOJE");

            var ontem = new ActionLogEntry
            {
                Tool = "write",
                Target = "a",
                FullTarget = "a",
                Timestamp = DateTime.Now.AddDays(-1)
            };
            ontem.DiaRotulo.Should().Be("ONTEM");
        }
    }
}
