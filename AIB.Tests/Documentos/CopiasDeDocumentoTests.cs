using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AIB.Services.Documentos;
using FluentAssertions;
using Xunit;

namespace AIB.Tests.Documentos;

/// <summary>
/// Testes das cópias de segurança de documentos (pedido do usuário de 19/10/2026):
/// últimas 5 por arquivo, rotação, falhas, restauração e isolamento em doc-backups.
/// </summary>
public sealed class CopiasDeDocumentoTests : IDisposable
{
    private readonly string _pasta;
    private readonly string _raiz;

    public CopiasDeDocumentoTests()
    {
        _pasta = Path.Combine(Path.GetTempPath(), "aib-doc-" + Guid.NewGuid().ToString("N"));
        _raiz = Path.Combine(_pasta, ".AIB");
        Directory.CreateDirectory(_raiz);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private string CriarArquivo(string nome, string conteudo = "dados")
    {
        string c = Path.Combine(_pasta, nome);
        File.WriteAllText(c, conteudo, Encoding.UTF8);
        return c;
    }

    [Fact]
    public void GuardarSeteVezes_DeixaAsCincoMaisNovas()
    {
        string arq = CriarArquivo("dados.xlsx", "original");
        DateTime relogio = new(2026, 10, 19, 10, 0, 0);
        var copias = new CopiasDeDocumento(_raiz, () => relogio);

        var caminhosCriados = new List<string>();
        for (int i = 1; i <= 7; i++)
        {
            relogio = relogio.AddMinutes(1);
            File.WriteAllText(arq, $"versao {i}");
            caminhosCriados.Add(copias.Guardar(arq));
        }

        var lista = copias.Copias(arq);
        lista.Should().HaveCount(CopiasDeDocumento.PorArquivo);
        lista.Should().Equal(caminhosCriados.Skip(2).Reverse());

        File.Exists(caminhosCriados[0]).Should().BeFalse("a mais velha foi podada");
        File.Exists(caminhosCriados[1]).Should().BeFalse("a segunda mais velha foi podada");
        foreach (var c in lista)
            File.Exists(c).Should().BeTrue();
    }

    [Fact]
    public void NomesComData_EColisaoNoMesmoSegundo()
    {
        string arq = CriarArquivo("relatorio.docx", "conteudo");
        DateTime relogio = new(2026, 10, 19, 14, 30, 15);
        var copias = new CopiasDeDocumento(_raiz, () => relogio);

        string copia1 = copias.Guardar(arq);
        Path.GetFileName(copia1).Should().Be("2026-10-19 143015.docx");

        string copia2 = copias.Guardar(arq);
        Path.GetFileName(copia2).Should().Be("2026-10-19 143015 (2).docx");

        string copia3 = copias.Guardar(arq);
        Path.GetFileName(copia3).Should().Be("2026-10-19 143015 (3).docx");

        copias.Copias(arq).Should().Equal(copia3, copia2, copia1);
    }

    [Fact]
    public void OrigemTxt_ContemOCaminhoOriginal()
    {
        string arq = CriarArquivo("planilha.xlsx", "123");
        var copias = new CopiasDeDocumento(_raiz);

        string copia = copias.Guardar(arq);
        string pastaArquivo = Path.GetDirectoryName(copia)!;
        string origemTxt = Path.Combine(pastaArquivo, "origem.txt");

        File.Exists(origemTxt).Should().BeTrue();
        File.ReadAllText(origemTxt, Encoding.UTF8).Should().Be(Path.GetFullPath(arq));
    }

    [Fact]
    public void Descricao_EDeterministica_EIgualComMaiusculasEMinusculas()
    {
        string arq = CriarArquivo("DocExemplo.docx", "abc");

        string d1 = CopiasDeDocumento.Descricao(arq);
        string d2 = CopiasDeDocumento.Descricao(arq);
        d1.Should().Be(d2);

        string dUpper = CopiasDeDocumento.Descricao(arq.ToUpperInvariant());
        string dLower = CopiasDeDocumento.Descricao(arq.ToLowerInvariant());

        dUpper.Should().Be(d1);
        dLower.Should().Be(d1);
        d1.Should().StartWith("~/.AIB/doc-backups/");
        d1.Should().Contain("DocExemplo");
    }

    [Fact]
    public void Limpar_ApagaSoAsComMaisDe90Dias_ERemovePastaVazia()
    {
        string arqVelho = CriarArquivo("velho.xlsx", "v1");
        string arqMisto = CriarArquivo("misto.xlsx", "m1");

        DateTime relogio = new(2026, 1, 1, 12, 0, 0);
        var copias = new CopiasDeDocumento(_raiz, () => relogio);

        string copiaVelho = copias.Guardar(arqVelho);
        string copiaMistoVelha = copias.Guardar(arqMisto);

        relogio = relogio.AddDays(100);
        string copiaMistoNova = copias.Guardar(arqMisto);

        int apagadas = copias.Limpar();
        apagadas.Should().Be(2);

        File.Exists(copiaVelho).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(copiaVelho)!).Should().BeFalse("pasta vazia é removida");

        File.Exists(copiaMistoVelha).Should().BeFalse();
        File.Exists(copiaMistoNova).Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(copiaMistoNova)!).Should().BeTrue("pasta com cópias válidas fica");
    }

    [Fact]
    public void Restaurar_VoltaOConteudo_EGuardaOEstadoAtualAntes()
    {
        string arq = CriarArquivo("documento.docx", "conteudo original");
        DateTime relogio = new(2026, 10, 19, 10, 0, 0);
        var copias = new CopiasDeDocumento(_raiz, () => relogio);

        string copiaOriginal = copias.Guardar(arq);

        relogio = relogio.AddMinutes(5);
        File.WriteAllText(arq, "conteudo gravado");
        string? impressaoDepois = Gravacao.Impressao(arq);

        relogio = relogio.AddMinutes(5);
        string msg = copias.Restaurar(arq, copiaOriginal, impressaoDepois);

        msg.Should().Be($"'{Path.GetFileName(arq)}' voltou ao que era antes da gravação.");
        File.ReadAllText(arq).Should().Be("conteudo original");

        var lista = copias.Copias(arq);
        lista.Should().HaveCount(2);
        File.ReadAllText(lista[0]).Should().Be("conteudo gravado", "o estado pré-desfazer virou cópia");
    }

    [Fact]
    public void Restaurar_RecusaSeOArquivoMudouDepois_ENaoTocaNoArquivo()
    {
        string arq = CriarArquivo("documento.docx", "versao 1");
        var copias = new CopiasDeDocumento(_raiz);

        string copia1 = copias.Guardar(arq);
        File.WriteAllText(arq, "versao 2");
        string? impressaoDepois = Gravacao.Impressao(arq);

        File.WriteAllText(arq, "versao 3 modificada no word");

        var acao = () => copias.Restaurar(arq, copia1, impressaoDepois);
        acao.Should().Throw<ErroDeDocumento>()
            .WithMessage($"*'{Path.GetFileName(arq)}' mudou depois da gravação; restaurar apagaria essas mudanças*");

        File.ReadAllText(arq).Should().Be("versao 3 modificada no word");
        copias.Copias(arq).Should().ContainSingle();
    }

    [Fact]
    public void Restaurar_ComCopiaInexistente_DaErroDeDocumento()
    {
        string arq = CriarArquivo("doc.docx", "texto");
        var copias = new CopiasDeDocumento(_raiz);
        string? impressao = Gravacao.Impressao(arq);

        var acao = () => copias.Restaurar(arq, Path.Combine(_raiz, "inexistente.docx"), impressao);
        acao.Should().Throw<ErroDeDocumento>().WithMessage("*não existe*");
    }

    [Fact]
    public void DoisArquivosDeMesmoNome_EmPastasDiferentes_NaoSeMisturam()
    {
        string pasta1 = Path.Combine(_pasta, "projetoA");
        string pasta2 = Path.Combine(_pasta, "projetoB");
        Directory.CreateDirectory(pasta1);
        Directory.CreateDirectory(pasta2);

        string arq1 = Path.Combine(pasta1, "relatorio.docx");
        string arq2 = Path.Combine(pasta2, "relatorio.docx");
        File.WriteAllText(arq1, "conteudo A");
        File.WriteAllText(arq2, "conteudo B");

        var copias = new CopiasDeDocumento(_raiz);
        string c1 = copias.Guardar(arq1);
        string c2 = copias.Guardar(arq2);

        Path.GetDirectoryName(c1).Should().NotBe(Path.GetDirectoryName(c2));
        copias.Copias(arq1).Should().ContainSingle().Which.Should().Be(c1);
        copias.Copias(arq2).Should().ContainSingle().Which.Should().Be(c2);

        File.ReadAllText(c1).Should().Be("conteudo A");
        File.ReadAllText(c2).Should().Be("conteudo B");
    }
}
