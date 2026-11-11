using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace AIB.Services.Documentos;

/// <summary>
/// Lê .docx e .pdf como <see cref="Documento"/>: blocos numerados, na ordem.
/// <para>
/// Sempre com FileShare.ReadWrite: o usuário pede "lê o relatório" com o relatório aberto no
/// Word, e o Word segura o arquivo para escrita. Abrir sem compartilhar daria "arquivo em uso".
/// </para>
/// </summary>
public static class LeitorDeDocumento
{
    public static Documento Abrir(string caminho)
    {
        return Formatos.De(caminho) switch
        {
            Formato.Docx => Docx(caminho),
            Formato.Pdf => Pdf(caminho),
            _ => throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' não é .docx nem .pdf (pelo conteúdo do arquivo).")
        };
    }

    private static Documento Docx(string caminho)
    {
        using var fs = Compartilhado(caminho);
        WordprocessingDocument doc;
        try
        {
            doc = WordprocessingDocument.Open(fs, false);
        }
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException)
        {
            throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' não é um .docx válido.");
        }

        using (doc)
            return new Documento(Formato.Docx, BlocosDoWord.Ler(doc).Select(i => i.Bloco));
    }

    /// <summary>Uma página, um bloco: o número que o modelo vê é o número da página.</summary>
    private static Documento Pdf(string caminho)
    {
        using var fs = Compartilhado(caminho);
        try
        {
            using var pdf = PdfDocument.Open(fs);
            // ContentOrderTextExtractor e não page.Text: page.Text cola as linhas ("fimdelinhacomeço").
            var blocos = pdf.GetPages()
                .Select(p => new Bloco(p.Number, TipoDeBloco.Pagina, 0, ContentOrderTextExtractor.GetText(p).Trim()))
                .ToList();
            return new Documento(Formato.Pdf, blocos, pdf.NumberOfPages);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new ErroDeDocumento("o PDF é protegido por senha; peça ao usuário uma cópia sem senha ou que cole o trecho que interessa.");
        }
        catch (UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            throw new ErroDeDocumento($"'{Path.GetFileName(caminho)}' está danificado: não deu para ler o PDF.");
        }
    }

    private static FileStream Compartilhado(string caminho) =>
        new(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
