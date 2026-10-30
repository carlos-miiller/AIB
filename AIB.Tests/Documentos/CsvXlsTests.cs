using System;
using System.IO;
using System.Linq;
using System.Text;
using AIB.Services.Documentos;
using FluentAssertions;

namespace AIB.Tests;

/// <summary>
/// Testes de leitura de CSV/TSV e .xls legado da ferramenta doc.
/// </summary>
public class CsvXlsTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-doc-" + Guid.NewGuid().ToString("N"));

    public CsvXlsTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private string CriarArquivo(string nome, byte[] bytes)
    {
        string caminho = Path.Combine(_pasta, nome);
        File.WriteAllBytes(caminho, bytes);
        return caminho;
    }

    // ─────────────────────────────────────────────────────────────────
    // CSV / TSV
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PontoEVirgula_ComNumerosPtBr_EFormatoInvariante()
    {
        // CSV típico pt-BR com ';' e números formatados com vírgula decimal e ponto de milhar
        var csv = "Nome;Salario;Bonus;Desconto\nAna;1.234,56;12,5;-3\nBruno;1234.56;0;-10";
        var caminho = CriarArquivo("dados.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, sep, _) = LeitorDeCsv.Ler(caminho);

        sep.Should().Be(';');
        grade[2, 2].Texto.Should().Be("1.234,56");
        grade[2, 2].Valor.Should().Be(1234.56);
        grade[2, 3].Texto.Should().Be("12,5");
        grade[2, 3].Valor.Should().Be(12.5);
        grade[2, 4].Texto.Should().Be("-3");
        grade[2, 4].Valor.Should().Be(-3.0);

        // Também aceita número invariante com ';'
        grade[3, 2].Texto.Should().Be("1234.56");
        grade[3, 2].Valor.Should().Be(1234.56);
    }

    [Fact]
    public void Virgula_AceitaApenasNumerosNoFormatoInvariante()
    {
        // Quando o separador é vírgula, decimais com vírgula são ambíguos e devem permanecer string
        var csv = "Nome,Valor,TextoComVirgula\nAna,1234.56,\"12,5\"\nBruno,-3,Outro";
        var caminho = CriarArquivo("invariante.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, sep, _) = LeitorDeCsv.Ler(caminho);

        sep.Should().Be(',');
        grade[2, 2].Texto.Should().Be("1234.56");
        grade[2, 2].Valor.Should().Be(1234.56);
        grade[2, 3].Texto.Should().Be("12,5");
        grade[2, 3].Valor.Should().Be("12,5", "com separador ',' vírgula decimal não é número invariante");
        grade[3, 2].Valor.Should().Be(-3.0);
    }

    [Fact]
    public void Aspas_ComSeparador_AspasDuplicadas_EQuebraDeLinha()
    {
        // RFC 4180: campos entre aspas contendo separador, aspas escapadas e quebra de linha.
        // A linha seguinte deve manter a numeração correta de linhas lógicas.
        var csv = "ID;Descricao;Status\n1;\"Item; com ponto e vírgula e \"\"aspas\"\"\ne quebra de linha\";OK\n2;Segunda linha;Ativo";
        var caminho = CriarArquivo("rfc4180.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, sep, _) = LeitorDeCsv.Ler(caminho);

        sep.Should().Be(';');
        grade.Linhas.Should().Be(3);
        grade[2, 1].Texto.Should().Be("1");
        grade[2, 2].Texto.Should().Be("Item; com ponto e vírgula e \"aspas\"\ne quebra de linha");
        grade[2, 3].Texto.Should().Be("OK");

        // Linha 3 continua com a numeração lógica esperada
        grade[3, 1].Texto.Should().Be("2");
        grade[3, 2].Texto.Should().Be("Segunda linha");
        grade[3, 3].Texto.Should().Be("Ativo");
    }

    [Fact]
    public void CelulaVaziaNoMeio_MantemPosicaoDasColunas()
    {
        // O erro clássico da ler-planilha: ignorava células vazias e "TI" caía na coluna de "Ramal"
        var csv = "Nome;Ramal;Setor\nAna;;TI\nBruno;2041;RH";
        var caminho = CriarArquivo("posicao.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, _, _) = LeitorDeCsv.Ler(caminho);

        grade[2, 1].Texto.Should().Be("Ana");
        grade[2, 2].EstaVazia.Should().BeTrue("coluna do meio vazia deve ser Celula.Vazia");
        grade[2, 3].Texto.Should().Be("TI");

        grade[3, 1].Texto.Should().Be("Bruno");
        grade[3, 2].Texto.Should().Be("2041");
        grade[3, 3].Texto.Should().Be("RH");
    }

    [Fact]
    public void Windows1252_ComAcentos_NaoFicaCorrompido()
    {
        // Caso real: "Recepção São José" virava "Recepo So Jos" ao ler arquivo ANSI sem detecção
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc1252 = Encoding.GetEncoding(1252);
        string conteudo = "Id;Local\n1;Recepção São José";
        var bytes = enc1252.GetBytes(conteudo);
        var caminho = CriarArquivo("ansi.csv", bytes);

        var (grade, _, codificacao) = LeitorDeCsv.Ler(caminho);

        codificacao.CodePage.Should().Be(1252);
        grade[2, 2].Texto.Should().Be("Recepção São José");
    }

    [Fact]
    public void Utf8ComBom_DevolveEncodingComPreambulo()
    {
        var utf8ComBom = new UTF8Encoding(true);
        string conteudo = "Nome;Ramal\nAna;2101";
        byte[] preambulo = utf8ComBom.GetPreamble();
        byte[] corpo = Encoding.UTF8.GetBytes(conteudo);
        byte[] todos = preambulo.Concat(corpo).ToArray();

        var caminho = CriarArquivo("bom.csv", todos);
        var (grade, sep, codificacao) = LeitorDeCsv.Ler(caminho);

        sep.Should().Be(';');
        codificacao.GetPreamble().Should().NotBeEmpty("BOM UTF-8 precisa retornar UTF8Encoding(true)");
        grade[2, 1].Texto.Should().Be("Ana");
        grade[2, 2].Texto.Should().Be("2101");
    }

    [Fact]
    public void Utf16LE_E_Utf16BE_ComBOM_DetectaEncodingCorreto()
    {
        string conteudo = "Nome;Valor\nAna;100";
        byte[] bytesLe = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(conteudo)).ToArray();
        var caminhoLe = CriarArquivo("utf16le.csv", bytesLe);
        var (gradeLe, _, codLe) = LeitorDeCsv.Ler(caminhoLe);
        codLe.Should().Be(Encoding.Unicode);
        gradeLe[2, 1].Texto.Should().Be("Ana");

        byte[] bytesBe = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(conteudo)).ToArray();
        var caminhoBe = CriarArquivo("utf16be.csv", bytesBe);
        var (gradeBe, _, codBe) = LeitorDeCsv.Ler(caminhoBe);
        codBe.Should().Be(Encoding.BigEndianUnicode);
        gradeBe[2, 1].Texto.Should().Be("Ana");
    }

    [Fact]
    public void ArquivoTsv_DetectaTabulacaoAutomaticamente()
    {
        string tsv = "Nome\tSetor\nCarlos\tEngenharia";
        var caminho = CriarArquivo("dados.tsv", Encoding.UTF8.GetBytes(tsv));

        var (grade, sep, _) = LeitorDeCsv.Ler(caminho);

        sep.Should().Be('\t');
        grade[2, 1].Texto.Should().Be("Carlos");
        grade[2, 2].Texto.Should().Be("Engenharia");
    }

    [Fact]
    public void DatasFormatadas_VirantDateTime()
    {
        string csv = "Nome;DataAdmissao;DataISO;DataHora\nAna;15/10/2024;2024-10-15;15/10/2024 14:30";
        var caminho = CriarArquivo("datas.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, _, _) = LeitorDeCsv.Ler(caminho);

        grade[2, 2].Texto.Should().Be("15/10/2024");
        grade[2, 2].Valor.Should().Be(new DateTime(2024, 10, 15));

        grade[2, 3].Texto.Should().Be("2024-10-15");
        grade[2, 3].Valor.Should().Be(new DateTime(2024, 10, 15));

        grade[2, 4].Texto.Should().Be("15/10/2024 14:30");
        grade[2, 4].Valor.Should().Be(new DateTime(2024, 10, 15, 14, 30, 0));
    }

    [Fact]
    public void EmpateDeSeparadores_EscolhePontoEVirgula()
    {
        // Empate de contagem consistente entre ';' e ',' deve priorizar ';' (padrão Excel pt-BR)
        string csv = "A;B,C\nD;E,F\nG;H,I";
        var caminho = CriarArquivo("empate.csv", Encoding.UTF8.GetBytes(csv));

        var (_, sep, _) = LeitorDeCsv.Ler(caminho);

        sep.Should().Be(';', "em empate de separadores consistentes, ';' é a preferência do Excel pt-BR");
    }

    [Fact]
    public void ArquivoDeUmaColunaSo_EscolheVirgula()
    {
        string csv = "PrimeiraLinha\nSegundaLinha\nTerceiraLinha";
        var caminho = CriarArquivo("uma_coluna.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, sep, _) = LeitorDeCsv.Ler(caminho);

        sep.Should().Be(',');
        grade.Linhas.Should().Be(3);
        grade[1, 1].Texto.Should().Be("PrimeiraLinha");
    }

    [Fact]
    public void LinhasVaziasNoFim_SaoDescartadas_MasNoMeioMantemContagem()
    {
        // Linhas vazias no meio mantêm a numeração do Excel; no fim são descartadas
        string csv = "Nome;Valor\n\nAna;10\n\n\n";
        var caminho = CriarArquivo("vazias.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, _, _) = LeitorDeCsv.Ler(caminho);

        grade.Linhas.Should().Be(3, "linha vazia no meio conta, linhas vazias finais são descartadas");
        grade[1, 1].Texto.Should().Be("Nome");
        grade.LinhaVazia(2).Should().BeTrue("a linha 2 era vazia no meio do arquivo");
        grade[3, 1].Texto.Should().Be("Ana");
        grade[3, 2].Valor.Should().Be(10.0);
    }

    [Fact]
    public void QuebrasDeLinha_AceitaCR_LF_ECRLF()
    {
        string csv = "A;B\rC;D\r\nE;F\nG;H";
        var caminho = CriarArquivo("quebras.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, _, _) = LeitorDeCsv.Ler(caminho);

        grade.Linhas.Should().Be(4);
        grade[1, 1].Texto.Should().Be("A");
        grade[2, 1].Texto.Should().Be("C");
        grade[3, 1].Texto.Should().Be("E");
        grade[4, 1].Texto.Should().Be("G");
    }

    [Fact]
    public void EspacosInternos_NaoSaoAparados_NoTexto()
    {
        // Espaços internos devem ser mantidos como o usuário escreveu
        string csv = "  Nome com espaço  ;  2101  ";
        var caminho = CriarArquivo("espacos.csv", Encoding.UTF8.GetBytes(csv));

        var (grade, _, _) = LeitorDeCsv.Ler(caminho);

        grade[1, 1].Texto.Should().Be("  Nome com espaço  ");
        grade[1, 2].Texto.Should().Be("  2101  ");
        grade[1, 2].Valor.Should().Be(2101.0, "o valor numérico reconhece mesmo com espaços nas pontas");
    }

    [Fact]
    public void TetoDeCelulas_LancaErroDeDocumentoQuandoExcedido()
    {
        // 3.000.001 células deve lançar ErroDeDocumento
        var sb = new StringBuilder();
        // 100 linhas com 30.001 colunas = 3.000.100 células
        for (int l = 0; l < 100; l++)
        {
            sb.Append(string.Join(";", Enumerable.Repeat("1", 30_001)));
            sb.Append('\n');
        }
        var caminho = CriarArquivo("gigante.csv", Encoding.UTF8.GetBytes(sb.ToString()));

        var act = () => LeitorDeCsv.Ler(caminho);

        act.Should().Throw<ErroDeDocumento>()
            .WithMessage("*grande demais*");
    }

    // ─────────────────────────────────────────────────────────────────
    // XLS (Excel 97-2003)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LeituraDeXlsReal_ArquivoRamais()
    {
        string caminho = Path.Combine(AppContext.BaseDirectory, "Documentos", "ramais.xls");
        File.Exists(caminho).Should().BeTrue($"ramais.xls deve estar presente na pasta Documentos: {caminho}");

        var pasta = LeitorDeXls.Abrir(caminho);

        pasta.Formato.Should().Be(Formato.Xls);
        pasta.Abas.Should().HaveCount(1);

        var aba = pasta.Aba("Ramais");
        aba.Should().NotBeNull();
        aba!.Nome.Should().Be("Ramais");
        aba.Oculta.Should().BeFalse();

        // Cabeçalho
        aba[1, 1].Texto.Should().Be("Nome");
        aba[1, 2].Texto.Should().Be("Ramal");
        aba[1, 3].Texto.Should().Be("Setor");
        aba[1, 4].Texto.Should().Be("Admissão");

        // Linha 2
        aba[2, 1].Texto.Should().Be("Ana Souza");
        aba[2, 2].Texto.Should().Be("2101", "números inteiros devem vir sem ',0'");
        aba[2, 2].Valor.Should().Be(2101.0);
        aba[2, 3].Texto.Should().Be("Financeiro");
        aba[2, 4].Texto.Should().Be("15/03/2021", "data deve vir formatada em dd/MM/yyyy");
        aba[2, 4].Valor.Should().Be(new DateTime(2021, 3, 15));

        // Linha 3
        aba[3, 1].Texto.Should().Be("João Lima");
        aba[3, 2].Texto.Should().Be("2102");
        aba[3, 2].Valor.Should().Be(2102.0);
        aba[3, 3].Texto.Should().Be("TI");
        aba[3, 4].EstaVazia.Should().BeTrue("célula vazia no fim da linha de dados");

        // Linha 4
        aba[4, 1].Texto.Should().Be("Márcia Reis");
        aba[4, 2].Texto.Should().Be("2103");
        aba[4, 2].Valor.Should().Be(2103.0);
        aba[4, 3].Texto.Should().Be("RH");
        aba[4, 4].EstaVazia.Should().BeTrue();
    }

    [Fact]
    public void XlsInvalidoOuCorrompido_LancaErroDeDocumento()
    {
        string caminho = CriarArquivo("corrompido.xls", new byte[] { 0x01, 0x02, 0x03, 0x04 });

        var act = () => LeitorDeXls.Abrir(caminho);

        act.Should().Throw<ErroDeDocumento>()
            .WithMessage("*não é um .xls válido*");
    }
}
