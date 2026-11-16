using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIB.Services.Documentos;

/// <summary>Os formatos que a ferramenta <c>doc</c> conhece.</summary>
public enum Formato { Desconhecido, Xlsx, Xls, Csv, Docx, Pdf }

public static class Formatos
{
    /// <summary>
    /// O formato pelo CONTEÚDO, com a extensão de desempate. A <c>ler-planilha</c> confiava na
    /// extensão, e um .xls que era xlsx por dentro (ou o contrário) virava um erro genérico.
    /// </summary>
    public static Formato De(string caminho)
    {
        string ext = Path.GetExtension(caminho).ToLowerInvariant();
        byte[] cabeca = new byte[8];
        int lidos = 0;

        try
        {
            using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            lidos = fs.Read(cabeca, 0, cabeca.Length);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        bool zip = lidos >= 4 && cabeca[0] == 0x50 && cabeca[1] == 0x4B;
        bool ole = lidos >= 8 && cabeca[0] == 0xD0 && cabeca[1] == 0xCF && cabeca[2] == 0x11 && cabeca[3] == 0xE0;
        bool pdf = lidos >= 4 && cabeca[0] == (byte)'%' && cabeca[1] == (byte)'P' && cabeca[2] == (byte)'D' && cabeca[3] == (byte)'F';

        if (pdf) return Formato.Pdf;

        // OLE2 é o .xls antigo, mas também o .doc antigo, que esta ferramenta não lê.
        if (ole) return ext is ".doc" or ".dot" or ".ppt" or ".msg" ? Formato.Desconhecido : Formato.Xls;
        if (zip) return PeloZip(caminho);

        return ext switch
        {
            ".xlsx" or ".xlsm" => lidos == 0 ? Formato.Xlsx : Formato.Desconhecido,
            ".docx" => lidos == 0 ? Formato.Docx : Formato.Desconhecido,
            ".csv" or ".tsv" => Formato.Csv,
            ".pdf" => Formato.Desconhecido,
            _ => Formato.Desconhecido
        };
    }

    /// <summary>Um ZIP pode ser planilha, Word ou outra coisa: quem diz é a peça principal dentro.</summary>
    private static Formato PeloZip(string caminho)
    {
        try
        {
            using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
            if (zip.GetEntry("xl/workbook.xml") != null) return Formato.Xlsx;
            if (zip.GetEntry("word/document.xml") != null) return Formato.Docx;
        }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return Formato.Desconhecido;
    }

    /// <summary>Pela extensão só — para arquivo que ainda não existe (criar).</summary>
    public static Formato PelaExtensao(string caminho) => Path.GetExtension(caminho).ToLowerInvariant() switch
    {
        ".xlsx" or ".xlsm" => Formato.Xlsx,
        ".csv" or ".tsv" => Formato.Csv,
        ".docx" => Formato.Docx,
        ".xls" => Formato.Xls,
        ".pdf" => Formato.Pdf,
        _ => Formato.Desconhecido
    };

    public static bool Planilha(Formato f) => f is Formato.Xlsx or Formato.Xls or Formato.Csv;

    /// <summary>O que a ferramenta grava. .xls e PDF só se leem.</summary>
    public static bool Gravavel(Formato f) => f is Formato.Xlsx or Formato.Csv or Formato.Docx;
}

/// <summary>
/// Uma célula como a planilha a mostra.
/// </summary>
/// <param name="Texto">O valor formatado como o Excel mostra ("15/10/2024", "12,5%"). Vazio se vazia.</param>
/// <param name="Valor">O valor bruto: double, bool, DateTime, string — ou null. É o que a consulta soma.</param>
/// <param name="Formula">A fórmula sem o "=", ou null.</param>
public sealed record Celula(string Texto, object? Valor = null, string? Formula = null)
{
    public static readonly Celula Vazia = new("");
    public bool EstaVazia => Texto.Length == 0 && Valor is null && Formula is null;
}

/// <summary>
/// Uma aba, já lida para a memória. Linha e coluna começam em 1, como no Excel.
/// <para>
/// A célula vazia ocupa o lugar dela. Era o erro da <c>ler-planilha</c>: o XML não grava célula
/// vazia, o script ignorava a posição, e "TI" aparecia debaixo de "Ramal".
/// </para>
/// </summary>
public sealed class Grade
{
    private readonly List<Celula[]> _linhas = new();

    public Grade(string nome, bool oculta = false)
    {
        Nome = nome;
        Oculta = oculta;
    }

    public string Nome { get; }
    public bool Oculta { get; }

    /// <summary>Linhas do Excel ocultas (por autofiltro ou à mão).</summary>
    public HashSet<int> Ocultas { get; } = new();

    /// <summary>Descrição curta do autofiltro ativo ("A1:F200 · Status = Aberto") ou null se não houver.</summary>
    public string? Filtro { get; set; }

    /// <summary>Linha da primeira célula usada (1 se a aba começa no alto).</summary>
    public int PrimeiraLinha { get; set; } = 1;

    public int Linhas => _linhas.Count == 0 ? 0 : PrimeiraLinha + _linhas.Count - 1;
    public int Colunas { get; private set; }

    /// <summary>Acrescenta a próxima linha. Linhas vazias contam: a numeração é a do Excel.</summary>
    public void Acrescentar(IReadOnlyList<Celula> celulas)
    {
        var linha = celulas.ToArray();
        _linhas.Add(linha);
        int usadas = UltimaUsada(linha);
        if (usadas > Colunas) Colunas = usadas;
    }

    public Celula this[int linha, int coluna]
    {
        get
        {
            int i = linha - PrimeiraLinha;
            if (i < 0 || i >= _linhas.Count || coluna < 1) return Celula.Vazia;
            var l = _linhas[i];
            return coluna <= l.Length ? l[coluna - 1] ?? Celula.Vazia : Celula.Vazia;
        }
    }

    public bool LinhaVazia(int linha)
    {
        for (int c = 1; c <= Colunas; c++)
            if (!this[linha, c].EstaVazia) return false;
        return true;
    }

    private static int UltimaUsada(Celula[] linha)
    {
        for (int c = linha.Length; c >= 1; c--)
            if (linha[c - 1] is { EstaVazia: false }) return c;
        return 0;
    }

    /// <summary>"A", "B", ... "Z", "AA" — a letra da coluna.</summary>
    public static string Letra(int coluna)
    {
        string s = "";
        while (coluna > 0)
        {
            int r = (coluna - 1) % 26;
            s = (char)('A' + r) + s;
            coluna = (coluna - 1) / 26;
        }
        return s;
    }

    /// <summary>"AA" → 27. Devolve 0 se não for letra de coluna.</summary>
    public static int Coluna(string letras)
    {
        int n = 0;
        foreach (char ch in letras.Trim().ToUpperInvariant())
        {
            if (ch < 'A' || ch > 'Z') return 0;
            n = n * 26 + (ch - 'A' + 1);
            if (n > 16384) return 0;
        }
        return n;
    }
}

/// <summary>Um arquivo de planilha inteiro: as abas na ordem em que o Excel as mostra.</summary>
public sealed class Pasta
{
    public Pasta(Formato formato, IEnumerable<Grade> abas)
    {
        Formato = formato;
        Abas = abas.ToList();
    }

    public Formato Formato { get; }
    public IReadOnlyList<Grade> Abas { get; }

    /// <summary>A aba pelo nome, sem diferenciar maiúsculas; null pega a primeira visível.</summary>
    public Grade? Aba(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome))
            return Abas.FirstOrDefault(a => !a.Oculta) ?? Abas.FirstOrDefault();

        return Abas.FirstOrDefault(a => string.Equals(a.Nome, nome.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>O tipo de um bloco de documento.</summary>
public enum TipoDeBloco { Paragrafo, Titulo, Item, Tabela, Pagina }

/// <summary>
/// Um bloco de um documento (Word ou PDF), na ordem em que aparece.
/// </summary>
/// <param name="Numero">O número que a ferramenta mostra (¶12) e que a edição usa para apontar.
/// Em PDF, o número da página.</param>
/// <param name="Nivel">Nível do título (1, 2...) ou do item de lista; 0 nos demais.</param>
/// <param name="Texto">O texto do bloco. Na tabela, vazio: o conteúdo está em <paramref name="Tabela"/>.</param>
/// <param name="Tabela">As linhas da tabela, célula por célula. Null fora de tabela.</param>
/// <param name="Secao">O título mais recente acima do bloco — "onde" o bloco está.</param>
public sealed record Bloco(int Numero, TipoDeBloco Tipo, int Nivel, string Texto,
                           IReadOnlyList<IReadOnlyList<string>>? Tabela = null, string? Secao = null);

/// <summary>Um documento lido: os blocos e, em PDF, quantas páginas.</summary>
public sealed class Documento
{
    public Documento(Formato formato, IEnumerable<Bloco> blocos, int paginas = 0)
    {
        Formato = formato;
        Blocos = blocos.ToList();
        Paginas = paginas;
    }

    public Formato Formato { get; }
    public IReadOnlyList<Bloco> Blocos { get; }
    public int Paginas { get; }
}
