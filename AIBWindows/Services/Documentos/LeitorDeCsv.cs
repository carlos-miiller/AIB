using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AIB.Services.Documentos;

/// <summary>
/// Lê arquivos .csv e .tsv preservando codificação, detectando separador e estruturando células.
/// </summary>
public static class LeitorDeCsv
{
    private const long TetoDeCelulas = LeitorDePlanilha.TetoDeCelulas;

    private static readonly CultureInfo CulturaPtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] FormatosDeData =
    {
        "dd/MM/yyyy",
        "dd/MM/yyyy HH:mm",
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss"
    };

    /// <summary>A grade, o separador detectado e a codificação do arquivo.</summary>
    public static (Grade Grade, char Separador, Encoding Codificacao) Ler(string caminho)
    {
        // Compartilhando leitura e escrita: arquivos abertos no Excel ou outro processo continuam legíveis.
        using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        byte[] bytes = new byte[fs.Length];
        int lidos = 0;
        while (lidos < bytes.Length)
        {
            int n = fs.Read(bytes, lidos, bytes.Length - lidos);
            if (n == 0) break;
            lidos += n;
        }

        var (codificacao, offset) = DetectarCodificacao(bytes);
        string texto = codificacao.GetString(bytes, offset, bytes.Length - offset);

        char separador = DetectarSeparador(caminho, texto);
        var linhasBrutas = ParsearLinhas(texto, separador, caminho);

        string nomeGrade = Path.GetFileNameWithoutExtension(caminho);
        var grade = new Grade(nomeGrade) { PrimeiraLinha = 1 };

        // Linhas vazias no fim do arquivo são descartadas (o arquivo frequentemente termina em \r\n).
        // Linhas vazias no meio mantêm a contagem para bater com as linhas do arquivo original.
        int ultimaComConteudo = -1;
        for (int r = linhasBrutas.Count - 1; r >= 0; r--)
        {
            if (linhasBrutas[r].Any(c => !c.EstaVazia))
            {
                ultimaComConteudo = r;
                break;
            }
        }

        for (int r = 0; r <= ultimaComConteudo; r++)
        {
            grade.Acrescentar(linhasBrutas[r]);
        }

        return (grade, separador, codificacao);
    }

    private static (Encoding Codificacao, int Offset) DetectarCodificacao(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true), 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);

        // Sem BOM: tenta UTF-8 estrito; caso falhe (como arquivos ANSI com acentos pt-BR), cai para Windows-1252.
        // Motivo: "Recepção São José" saía "Recepo So Jos" quando interpretado cegamente como UTF-8 sem BOM.
        try
        {
            var utf8Estrito = new UTF8Encoding(false, true);
            utf8Estrito.GetCharCount(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (Encoding.GetEncoding(1252), 0);
        }
    }

    private static char DetectarSeparador(string caminho, string texto)
    {
        if (caminho.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
            return '\t';

        // Conta FORA de aspas nas primeiras ~20 linhas lógicas.
        var contagensPorLinha = new List<Dictionary<char, int>>();
        var linhaAtual = new Dictionary<char, int> { { ';', 0 }, { ',', 0 }, { '\t', 0 }, { '|', 0 } };

        bool dentroDeAspas = false;
        bool linhaTemConteudo = false;
        int i = 0;
        while (i < texto.Length && contagensPorLinha.Count < 20)
        {
            char ch = texto[i];
            if (ch == '"')
            {
                linhaTemConteudo = true;
                if (dentroDeAspas && i + 1 < texto.Length && texto[i + 1] == '"')
                {
                    i++; // aspas duplas "" dentro de aspas
                }
                else
                {
                    dentroDeAspas = !dentroDeAspas;
                }
            }
            else if (!dentroDeAspas)
            {
                if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < texto.Length && texto[i + 1] == '\n')
                    {
                        i++;
                    }
                    if (linhaTemConteudo)
                    {
                        contagensPorLinha.Add(linhaAtual);
                    }
                    linhaAtual = new Dictionary<char, int> { { ';', 0 }, { ',', 0 }, { '\t', 0 }, { '|', 0 } };
                    linhaTemConteudo = false;
                }
                else
                {
                    if (!char.IsWhiteSpace(ch))
                    {
                        linhaTemConteudo = true;
                    }
                    if (linhaAtual.ContainsKey(ch))
                    {
                        linhaAtual[ch]++;
                    }
                }
            }
            else
            {
                linhaTemConteudo = true;
            }
            i++;
        }

        if (linhaTemConteudo && contagensPorLinha.Count < 20)
        {
            contagensPorLinha.Add(linhaAtual);
        }

        int totalLinhas = contagensPorLinha.Count;
        if (totalLinhas == 0) return ',';

        char melhorSeparador = ',';
        int melhorTotalOcorrencias = 0;
        int melhorLinhasConsistentes = 0;
        bool encontrou = false;

        // Ordem de preferência para desempate: ';' > ',' > '\t' > '|'
        foreach (char cand in new[] { ';', ',', '\t', '|' })
        {
            var grupos = contagensPorLinha
                .Select(l => l[cand])
                .Where(cnt => cnt > 0)
                .GroupBy(cnt => cnt)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .FirstOrDefault();

            if (grupos == null) continue;

            int contagem = grupos.Key;
            int linhasComContagem = grupos.Count();

            // Deve aparecer na maioria das linhas lógicas (> metade)
            if (linhasComContagem > totalLinhas / 2)
            {
                int totalOcorrencias = contagem * linhasComContagem;

                if (!encontrou)
                {
                    melhorSeparador = cand;
                    melhorTotalOcorrencias = totalOcorrencias;
                    melhorLinhasConsistentes = linhasComContagem;
                    encontrou = true;
                }
                else
                {
                    if (totalOcorrencias > melhorTotalOcorrencias)
                    {
                        melhorSeparador = cand;
                        melhorTotalOcorrencias = totalOcorrencias;
                        melhorLinhasConsistentes = linhasComContagem;
                    }
                    else if (totalOcorrencias == melhorTotalOcorrencias && cand == ';')
                    {
                        melhorSeparador = ';';
                    }
                }
            }
        }

        return encontrou ? melhorSeparador : ',';
    }

    private static List<List<Celula>> ParsearLinhas(string texto, char separador, string caminho)
    {
        var linhas = new List<List<Celula>>();
        var linhaAtual = new List<Celula>();
        var sb = new StringBuilder();
        bool dentroDeAspas = false;
        bool campoTeveAspas = false;
        long totalCelulas = 0;

        void AdicionarCampo()
        {
            string textoCampo = sb.ToString();
            sb.Clear();

            // Células vazias mantêm a posição original para evitar desalinhamento de colunas.
            Celula celula = (!campoTeveAspas && textoCampo.Length == 0)
                ? Celula.Vazia
                : ConverterCelula(textoCampo, separador);

            campoTeveAspas = false;
            linhaAtual.Add(celula);
            totalCelulas++;

            if (totalCelulas > TetoDeCelulas)
                throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' passa de {TetoDeCelulas:N0} células; grande demais para ler de uma vez.");
        }

        void FinalizarLinha()
        {
            AdicionarCampo();
            linhas.Add(linhaAtual);
            linhaAtual = new List<Celula>();
        }

        for (int i = 0; i < texto.Length; i++)
        {
            char ch = texto[i];

            if (dentroDeAspas)
            {
                if (ch == '"')
                {
                    if (i + 1 < texto.Length && texto[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        dentroDeAspas = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else
            {
                if (ch == '"')
                {
                    dentroDeAspas = true;
                    campoTeveAspas = true;
                }
                else if (ch == separador)
                {
                    AdicionarCampo();
                }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < texto.Length && texto[i + 1] == '\n')
                    {
                        i++;
                    }
                    FinalizarLinha();
                }
                else
                {
                    sb.Append(ch);
                }
            }
        }

        if (linhaAtual.Count > 0 || sb.Length > 0 || campoTeveAspas)
        {
            AdicionarCampo();
            linhas.Add(linhaAtual);
        }

        return linhas;
    }

    private static Celula ConverterCelula(string texto, char separador)
    {
        if (string.IsNullOrEmpty(texto))
            return Celula.Vazia;

        string aparado = texto.Trim();

        // Datas reconhecidas viram DateTime
        if (DateTime.TryParseExact(aparado, FormatosDeData, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime data))
            return new Celula(texto, data, null);

        // Números: com ';' ou '\t' prefere pt-BR ("1.234,56", "12,5", "-3") e aceita invariante ("1234.56").
        // Com ',' só aceita invariante (para não engolir vírgulas que não sejam delimitadores).
        if (separador is ';' or '\t')
        {
            if (aparado.Contains(','))
            {
                if (double.TryParse(aparado, NumberStyles.Float | NumberStyles.AllowThousands, CulturaPtBr, out double numPt))
                    return new Celula(texto, numPt, null);
            }
            else
            {
                if (double.TryParse(aparado, NumberStyles.Float, CultureInfo.InvariantCulture, out double numInv))
                    return new Celula(texto, numInv, null);

                // Caso especial de inteiro com pontos de milhar em pt-BR (ex.: "1.000.000")
                if (double.TryParse(aparado, NumberStyles.Integer | NumberStyles.AllowThousands, CulturaPtBr, out double numPtInt))
                    return new Celula(texto, numPtInt, null);
            }
        }
        else
        {
            if (double.TryParse(aparado, NumberStyles.Float, CultureInfo.InvariantCulture, out double numInv))
                return new Celula(texto, numInv, null);
        }

        // Senão preserva como string bruta (espaços internos do texto não são aparados).
        return new Celula(texto, texto, null);
    }
}
