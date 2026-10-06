using System;
using AIB.Services;
using AIB.Services.Memory;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Como cada ferramenta se descreve no registro de ações e nos tooltips.
    /// <para>
    /// A falha que estes ensaios prendem: só <c>read</c>, <c>write</c>, <c>shell</c> e
    /// <c>skill</c> tinham extrator. <c>edit</c>, <c>glob</c>, <c>grep</c> e <c>mail</c> chegavam
    /// sem artefato, e o registro punha o NOME no lugar do alvo — a linha lia "edit edit", e o
    /// tooltip da trilha dizia só "edit", sem arquivo nenhum.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class DescricaoDasAcoesTests : IDisposable
    {
        public DescricaoDasAcoesTests() => ActionLogService.Clear();

        public void Dispose() => ActionLogService.Clear();

        private const string ArgsEdit =
            """{"path":"C:\\temp\\pagina.html","old_string":"<b>Ana</b>","new_string":"<b>Bia</b>\n<i>nova</i>"}""";

        [Fact]
        public void Edicao_ViraArtefatoDeGravacao_ComAsLinhasTrocadas()
        {
            var artefato = ArtifactExtractor.Construir(
                "edit", ArgsEdit, @"SUCESSO: 1 troca(s) em 'C:\temp\pagina.html'.");

            artefato.Should().NotBeNull("editar grava o arquivo, e a memória precisa saber");
            artefato!.Kind.Should().Be(ArtifactKind.FileWritten);
            artefato.Value.Should().Be(@"C:\temp\pagina.html");
            artefato.Detail.Should().Be("+2 linhas, −1 linha");
        }

        [Fact]
        public void Edicao_GuardaOAntesEODepois_ParaOTooltip()
        {
            var troca = ArtifactExtractor.TrocaDaEdicao("edit", ArgsEdit);

            troca.Should().NotBeNull();
            troca!.Antes.Should().Be("<b>Ana</b>");
            troca.Depois.Should().Be("<b>Bia</b>\n<i>nova</i>");

            ArtifactExtractor.TrocaDaEdicao("write", """{"path":"C:\\a.txt","content":"x"}""")
                .Should().BeNull("o write não guarda o conteúdo anterior — um antes inventado seria pior");
        }

        [Fact]
        public void AntesEDepois_TiraORecuoComum_SemPerderOAlinhamento()
        {
            // Visto na tela: HTML com três tabulações antes do texto. Na caixa de 340px sem
            // quebra, o recuo empurrava o conteúdo para fora e a linha mostrava só o "−".
            var troca = new TrocaDeTexto(
                "\t\t\t<span style=\"font-size: 12px\">",
                "\t\t\t<span style=\"font-size: 14px\">\n\t\t\t\t<b>Thais</b>");

            var (antes, depois) = troca.LinhasParaExibir();

            antes.Should().Equal("<span style=\"font-size: 12px\">");
            depois.Should().Equal("<span style=\"font-size: 14px\">", "    <b>Thais</b>");
        }

        [Fact]
        public void AntesEDepois_TrechoApagado_NaoTemLinhaDeDepois()
        {
            new TrocaDeTexto("  x", "").LinhasParaExibir().Depois.Should().BeEmpty();
        }

        [Fact]
        public void AntesEDepois_TemTetoPorLado()
        {
            string args = $$"""{"path":"C:\\a.txt","old_string":"{{new string('a', 5000)}}","new_string":"b"}""";

            var troca = ArtifactExtractor.TrocaDaEdicao("edit", args)!;

            troca.Antes.Length.Should().BeLessThanOrEqualTo(ArtifactExtractor.TetoDaTroca + 1);
            troca.Depois.Should().Be("b");
        }

        [Fact]
        public void EdicaoNoRegistro_LevaOAntesEODepois()
        {
            var troca = ArtifactExtractor.TrocaDaEdicao("edit", ArgsEdit);

            ChatWindow.RegistrarAcao(new ChatStreamItem.ToolFinished(
                "1", "edit", Failed: false, Denied: false, Artifact: null, Detail: null,
                Argument: ArtifactExtractor.ResumirArgumento("edit", ArgsEdit),
                Change: troca));

            ActionLogService.Entries[0].Troca.Should().Be(troca);
        }

        [Fact]
        public void Edicao_ComVariasTrocas_MultiplicaAsLinhas()
        {
            const string args =
                """{"path":"C:\\temp\\a.txt","old_string":"x","new_string":"y","replace_all":true}""";

            ArtifactExtractor.Construir("edit", args, @"SUCESSO: 3 troca(s) em 'C:\temp\a.txt'.")!
                .Detail.Should().Be("+3 linhas, −3 linhas");
        }

        [Fact]
        public void EdicaoRecusada_ViraRecusaComOCaminho()
        {
            var artefato = ArtifactExtractor.Construir("edit", ArgsEdit, "Ação Rejeitada pelo Usuário.");

            artefato!.Kind.Should().Be(ArtifactKind.Denied);
            artefato.Value.Should().Be(@"C:\temp\pagina.html");
        }

        [Fact]
        public void EdicaoNoRegistro_MostraOCaminho_ENaoONomeDuasVezes()
        {
            var artefato = ArtifactExtractor.Construir(
                "edit", ArgsEdit, @"SUCESSO: 1 troca(s) em 'C:\temp\pagina.html'.");

            ChatWindow.RegistrarAcao(new ChatStreamItem.ToolFinished(
                "1", "edit", Failed: false, Denied: false, artefato, Detail: null,
                Argument: ArtifactExtractor.ResumirArgumento("edit", ArgsEdit)));

            var entrada = ActionLogService.Entries[0];
            entrada.Tool.Should().Be("edit");
            entrada.Target.Should().NotBe("edit");
            entrada.FullTarget.Should().Be(@"C:\temp\pagina.html");
            entrada.Result.Should().Be("+2 linhas, −1 linha");
        }

        [Fact]
        public void BuscaSemArtefato_NoRegistro_DizOQueProcurouEOQueAchou()
        {
            const string args = """{"pattern":"*.html","path":"C:\\temp\\emails fisio"}""";
            const string resultado =
                "2 arquivo(s) para '*.html', do mais recente:\n"
                + "C:\\temp\\emails fisio\\a.html\nC:\\temp\\emails fisio\\b.html";

            ArtifactExtractor.Construir("glob", args, resultado)
                .Should().BeNull("busca não é fato para a memória — só a tela precisa dela");

            ChatWindow.RegistrarAcao(new ChatStreamItem.ToolFinished(
                "1", "glob", Failed: false, Denied: false, Artifact: null, Detail: null,
                Argument: ArtifactExtractor.ResumirArgumento("glob", args),
                Summary: ArtifactExtractor.ResumirResultado("glob", resultado),
                RawOutput: ArtifactExtractor.SaidaBruta("glob", resultado)));

            var entrada = ActionLogService.Entries[0];
            entrada.FullTarget.Should().Be(@"*.html em C:\temp\emails fisio");
            entrada.Result.Should().Be("2 arquivos");
            entrada.RotuloDoAlvo.Should().Be("BUSCA");
            entrada.Command.Should().BeNull("uma busca não é linha de comando");
            entrada.SoLeitura.Should().BeTrue("busca é leitura e sai em cinza, sem competir com escrita");
            entrada.RawOutput.Should().Contain("b.html");
        }

        [Theory]
        [InlineData("grep", "3 acerto(s) em 2 arquivo(s):\nC:\\a.cs:1: x", "3 acertos em 2 arquivos")]
        [InlineData("grep", "Nada casa com 'x' em 'C:\\t' (arquivos: *). 10 arquivo(s) examinado(s).", "nenhum acerto")]
        [InlineData("glob", "Nenhum arquivo casa com '*.x' em 'C:\\t'.", "nenhum arquivo")]
        [InlineData("glob", "1 arquivo(s) para 'a.*', do mais recente:\nC:\\t\\a.txt", "1 arquivo")]
        [InlineData("read", "'C:\\t' é uma PASTA, com 1 subpasta(s) e 3 arquivo(s):\n  sub\\", "3 arquivos, 1 pasta")]
        [InlineData("read", "'C:\\t' é uma pasta, e está vazia.", "pasta vazia")]
        [InlineData("read", "     1\tolá\n     2\tmundo", "2 linhas lidas")]
        [InlineData("shell", "a\nb\n\nc", "saída: 3 linhas")]
        [InlineData("shell", "Comando executado com sucesso (sem saída).", "sem saída")]
        public void ResumoDoResultado(string ferramenta, string resultado, string esperado)
        {
            ArtifactExtractor.ResumirResultado(ferramenta, resultado).Should().Be(esperado);
        }

        [Fact]
        public void ResultadoDeErro_NaoViraResumo()
        {
            // O erro já tem lugar próprio na linha, em vermelho. Um "resumo" dele seria o mesmo
            // texto dito duas vezes, uma delas errada.
            ArtifactExtractor.ResumirResultado("glob", "ERRO: a pasta 'x' não existe.").Should().BeNull();
        }

        [Fact]
        public void FormatoDesconhecido_NaoInventaResumo()
        {
            ArtifactExtractor.ResumirResultado("mail", "Última passada: 15/09 12:55.").Should().BeNull();
            ArtifactExtractor.ResumirResultado("glob", "resposta num formato que mudou").Should().BeNull();
        }

        [Fact]
        public void ConsultaDeEmail_DizOPeriodoEOsFiltros()
        {
            ArtifactExtractor.ResumirArgumento("mail",
                    """{"periodo":"semana","urgencia":"maxima","remetente":"ana"}""")
                .Should().Be("semana • urgência maxima • de: ana");

            ArtifactExtractor.ResumirArgumento("mail", "{}").Should().Be("hoje", "é o padrão da ferramenta");
        }

        [Fact]
        public void BuscaSemPasta_MostraAPastaQueAFerramentaUsa()
        {
            string casa = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            ArtifactExtractor.ResumirArgumento("grep", """{"pattern":"TODO","glob":"*.cs"}""")
                .Should().Be($"TODO em {casa} (*.cs)");
        }

        [Fact]
        public void SaidaBruta_SoDeComandoEBusca_EComTeto()
        {
            ArtifactExtractor.SaidaBruta("read", "conteúdo do arquivo")
                .Should().BeNull("a saída do read é o arquivo inteiro — não vira segunda cópia em memória");
            ArtifactExtractor.SaidaBruta("mail", "veredito").Should().BeNull();

            ArtifactExtractor.SaidaBruta("shell", new string('x', 10_000))!.Length
                .Should().BeLessThanOrEqualTo(ArtifactExtractor.TetoDaSaidaBruta + 2);
        }
    }
}
