using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ExcelDataReader;

namespace AIB.Services.Documentos;

/// <summary>
/// Lê planilhas no formato .xls antigo (BIFF / Excel 97-2003) via ExcelDataReader.
/// </summary>
public static class LeitorDeXls
{
    private static readonly CultureInfo CulturaPtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static Pasta Abrir(string caminho)
    {
        // Necessário para o ExcelDataReader decodificar strings com code pages legadas.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            // Compartilhamento ReadWrite|Delete para permitir ler mesmo com o Excel aberto.
            using var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = ExcelReaderFactory.CreateReader(fs);

            if (reader == null)
                throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' não é um .xls válido.");

            var abas = new List<Grade>();

            do
            {
                string nome = string.IsNullOrWhiteSpace(reader.Name) ? "Planilha" : reader.Name;
                bool oculta = reader.VisibleState != "visible";
                var grade = new Grade(nome, oculta) { PrimeiraLinha = 1 };

                var linhasDaAba = new List<Celula[]>();

                while (reader.Read())
                {
                    int fieldCount = reader.FieldCount;
                    var linha = new Celula[fieldCount];
                    for (int i = 0; i < fieldCount; i++)
                    {
                        linha[i] = ConverterCelula(reader, i);
                    }
                    linhasDaAba.Add(linha);
                }

                // Linhas vazias no fim de cada aba são descartadas para evitar ruído.
                int ultimaComConteudo = -1;
                for (int r = linhasDaAba.Count - 1; r >= 0; r--)
                {
                    if (linhasDaAba[r].Any(c => !c.EstaVazia))
                    {
                        ultimaComConteudo = r;
                        break;
                    }
                }

                for (int r = 0; r <= ultimaComConteudo; r++)
                {
                    grade.Acrescentar(linhasDaAba[r]);
                }

                abas.Add(grade);

            } while (reader.NextResult());

            return new Pasta(Formato.Xls, abas);
        }
        catch (ErroDeDocumento)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' não é um .xls válido: {ex.Message}");
        }
    }

    private static Celula ConverterCelula(IExcelDataReader reader, int indice)
    {
        object? valor = reader.GetValue(indice);
        if (valor is null || valor is DBNull)
            return Celula.Vazia;

        if (valor is string s)
        {
            return s.Length == 0 ? Celula.Vazia : new Celula(s, s, null);
        }

        if (valor is bool b)
        {
            return new Celula(b ? "VERDADEIRO" : "FALSO", b, null);
        }

        if (valor is DateTime dt)
        {
            string texto = dt.TimeOfDay == TimeSpan.Zero
                ? dt.ToString("dd/MM/yyyy")
                : dt.ToString("dd/MM/yyyy HH:mm");
            return new Celula(texto, dt, null);
        }

        if (valor is double or float or decimal or int or long or short or byte or sbyte)
        {
            double d = Convert.ToDouble(valor, CultureInfo.InvariantCulture);
            string? formato = reader.GetNumberFormatString(indice);

            if (formato != null && formato.Contains('%'))
            {
                double pct = d * 100.0;
                bool ehInteiroPct = Math.Abs(pct - Math.Round(pct)) < 1e-9;
                string textoPct = ehInteiroPct
                    ? ((long)Math.Round(pct)).ToString(CultureInfo.InvariantCulture) + "%"
                    : pct.ToString("#,##0.##########", CulturaPtBr) + "%";
                return new Celula(textoPct, d, null);
            }

            // Números inteiros saem sem casas (ex.: "2101"), decimais no padrão pt-BR ("1.234,5").
            bool ehInteiro = Math.Abs(d - Math.Round(d)) < 1e-9;
            string textoNum = ehInteiro
                ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture)
                : d.ToString("#,##0.##########", CulturaPtBr);

            return new Celula(textoNum, d, null);
        }

        string outro = valor.ToString() ?? "";
        return outro.Length == 0 ? Celula.Vazia : new Celula(outro, valor, null);
    }
}
