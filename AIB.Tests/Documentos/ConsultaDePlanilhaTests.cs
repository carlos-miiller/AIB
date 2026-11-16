using System;
using System.Linq;
using System.Text.Json;
using AIB.Services.Documentos;
using FluentAssertions;

namespace AIB.Tests;

public class ConsultaDePlanilhaTests
{
    private static Celula C(string texto) => new(texto, texto.Length == 0 ? null : texto);
    private static Celula N(double v, string? texto = null) => new(texto ?? v.ToString(), v);
    private static Celula D(DateTime d) => new(d.ToString("dd/MM/yyyy"), d);

    private static Pasta Pasta(Grade g) => new(Formato.Xlsx, new[] { g });

    private static string Q(Pasta p, string json, int teto = 20_000, string? aba = null)
    {
        using var doc = JsonDocument.Parse(json);
        return ConsultaDePlanilha.Executar(p, aba, doc.RootElement, teto);
    }

    /// <summary>Nome | Setor | Horas | Cidade | Data — com "n/d" e vazio em Horas.</summary>
    private static Pasta Horas()
    {
        var g = new Grade("Horas");
        g.Acrescentar(new[] { C("Nome"), C("Setor"), C("Horas"), C("Cidade"), C("Data") });
        g.Acrescentar(new[] { C("Ana"), C("TI"), N(2.5), C("São José"), D(new DateTime(2026, 10, 19)) });
        g.Acrescentar(new[] { C("Bruno"), C("RH"), N(4), C("Curitiba"), D(new DateTime(2026, 10, 18)) });
        g.Acrescentar(new[] { C("Carla"), C("TI"), C("n/d"), C("Curitiba"), D(new DateTime(2026, 10, 19)) });
        g.Acrescentar(new[] { C(""), C(""), C(""), C(""), C("") });
        g.Acrescentar(new[] { C("Davi"), C("TI"), N(6), C("Joinville"), D(new DateTime(2026, 10, 20)) });
        g.Acrescentar(new[] { C("Eva"), C("Fin"), C(""), C("SÃO JOSÉ"), D(new DateTime(2026, 9, 1)) });
        return Pasta(g);
    }

    [Fact]
    public void Soma_Simples()
    {
        var p = Pasta(Montar(new[] { "Valor" }, new[] { N(10) }, new[] { N(20.5) }, new[] { N(1000) }));
        string r = Q(p, """{"agg":[{"fn":"sum","col":"Valor"}]}""");
        r.Should().Contain("sum(Valor)\t1.030,5");
        r.Should().Contain("3 linhas de dados (cabeçalho na linha 1)");
    }

    // O modelo somava a coluna inteira de cabeça e não dizia que "n/d" ficou de fora.
    [Fact]
    public void Soma_ComTextoMisturado_IgnoraEInforma()
    {
        string r = Q(Horas(), """{"agg":[{"fn":"sum","col":"Horas"},{"fn":"avg","col":"Horas"}]}""");
        r.Should().Contain("sum(Horas)\t12,5");
        r.Should().Contain("avg(Horas)\t4,17");
        r.Should().Contain("1 célula não numérica ignorada em Horas");
    }

    [Fact]
    public void Filtro_NumeroPtBrEmTexto()
    {
        // CSV brasileiro: tudo chega como texto, com milhar e vírgula.
        var p = Pasta(Montar(new[] { "Item", "Preço" },
            new[] { C("a"), C("1.234,56") }, new[] { C("b"), C("999,99") }, new[] { C("c"), C("2.000") }));
        string r = Q(p, """{"where":[{"col":"Preço","op":">","value":"1.000"}],"agg":[{"fn":"sum","col":"Preço"},{"fn":"count"}]}""");
        r.Should().Contain("→ 2 linhas");
        r.Should().Contain("sum(Preço)\t3.234,56");
        r.Should().Contain("count\t2");
    }

    [Fact]
    public void Filtro_PorData()
    {
        string r = Q(Horas(), """{"where":[{"col":"Data","op":">=","value":"19/10/2026"}],"agg":["count","max(Data)"]}""");
        r.Should().Contain("count\t3");
        r.Should().Contain("max(Data)\t20/10/2026");

        Q(Horas(), """{"where":[{"col":"Data","value":"2026-10-19"}]}""").Should().Contain("→ 2 linhas");
    }

    [Fact]
    public void Contains_SemAcento_AchaComAcento()
    {
        string r = Q(Horas(), """{"where":[{"col":"Cidade","op":"contains","value":"sao jose"}]}""");
        r.Should().Contain("→ 2 linhas");
        r.Should().Contain("2\tAna\tTI").And.Contain("7\tEva\tFin");
    }

    [Fact]
    public void Grupo_SomaOrdenadaDecrescente_ComLimite()
    {
        string r = Q(Horas(), """{"group":"Setor","agg":[{"fn":"count"},{"fn":"sum","col":"Horas"}],"sort":"-sum(Horas)","limit":2}""");
        var linhas = r.Split('\n');
        linhas[1].Should().Be("Setor\tcount\tsum(Horas)");
        linhas[2].Should().Be("TI\t3\t8,5");
        linhas[3].Should().Be("RH\t1\t4");
        r.Should().Contain("[3 grupos; mostrando 2");
        r.Should().NotContain("Fin\t");
    }

    [Fact]
    public void Grupo_TemLinhaTotal()
    {
        string r = Q(Horas(), """{"group":"Setor","agg":[{"fn":"sum","col":"Horas"}]}""");
        r.Should().Contain("TI\t8,5").And.Contain("RH\t4").And.Contain("Fin\t—");
        r.Should().Contain("TOTAL\t12,5");
        // sem sort: ordem de aparição
        r.IndexOf("TI\t", StringComparison.Ordinal).Should().BeLessThan(r.IndexOf("RH\t", StringComparison.Ordinal));
    }

    [Fact]
    public void Grupo_SemAgg_ContaLinhas()
    {
        string r = Q(Horas(), """{"group":"Cidade"}""");
        r.Should().Contain("Curitiba\t2").And.Contain("TOTAL\t5");
    }

    [Fact]
    public void Count_SemCol_ContaLinhas_ComCol_ContaPreenchidas()
    {
        string r = Q(Horas(), """{"agg":[{"fn":"count"},{"fn":"count","col":"Horas"}]}""");
        r.Should().Contain("count\t5");
        r.Should().Contain("count(Horas)\t4");
    }

    [Fact]
    public void Coluna_PorLetra_EPorNome()
    {
        Q(Horas(), """{"agg":[{"fn":"sum","col":"C"}]}""").Should().Contain("sum(Horas)\t12,5");
        Q(Horas(), """{"agg":[{"fn":"sum","col":"horas"}]}""").Should().Contain("sum(Horas)\t12,5");
        Q(Horas(), """{"agg":[{"fn":"sum","col":"Hora"}]}""").Should().Contain("sum(Horas)\t12,5");
    }

    [Fact]
    public void CabecalhoChamadoA_VenceALetra()
    {
        var p = Pasta(Montar(new[] { "Id", "A" }, new[] { N(100), N(1) }, new[] { N(200), N(2) }));
        Q(p, """{"agg":[{"fn":"sum","col":"A"}]}""").Should().Contain("sum(A)\t3");
    }

    [Fact]
    public void ColunaInexistente_ErroListaCabecalhos()
    {
        var acao = () => Q(Horas(), """{"agg":[{"fn":"sum","col":"Salário"}]}""");
        acao.Should().Throw<ErroDeDocumento>()
            .Which.Message.Should().Contain("\"Salário\"").And.Contain("A Nome | B Setor | C Horas | D Cidade | E Data");
    }

    [Fact]
    public void ChaveDesconhecida_Erro()
    {
        var acao = () => Q(Horas(), """{"filter":[]}""");
        acao.Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("where");
    }

    [Fact]
    public void Linhas_SemAgg_TrazemONumeroDaLinhaDoExcel()
    {
        // A tabela começa na linha 5 (título e espaços acima): o número tem de ser o do Excel.
        var g = new Grade("Ramais") { PrimeiraLinha = 5 };
        g.Acrescentar(new[] { C("Nome"), C("Ramal"), C("Setor") });
        g.Acrescentar(new[] { C("Ana"), N(2041), C("TI") });
        g.Acrescentar(new[] { C("Bia"), N(2042), C("RH") });
        string r = Q(Pasta(g), """{"where":[{"col":"Setor","value":"ti"}]}""");
        r.Should().Contain("(cabeçalho na linha 5)");
        r.Should().Contain("linha\tNome\tRamal\tSetor");
        r.Should().Contain("6\tAna\t2041\tTI");
        r.Should().NotContain("Bia");
    }

    [Fact]
    public void Linhas_OrdenadasELimitadas()
    {
        string r = Q(Horas(), """{"sort":"-Horas","limit":2}""");
        var linhas = r.Split('\n');
        linhas[2].Should().StartWith("6\tDavi");
        linhas[3].Should().StartWith("3\tBruno");
        r.Should().Contain("[5 linhas; mostrando 2");
    }

    [Fact]
    public void Teto_DeCaracteres_Respeitado()
    {
        var g = new Grade("Grande");
        g.Acrescentar(new[] { C("Nome"), C("Texto") });
        for (int i = 0; i < 400; i++) g.Acrescentar(new[] { C("pessoa " + i), C(new string('x', 100)) });

        string r = Q(Pasta(g), """{"limit":500}""", teto: 2000);
        r.Length.Should().BeLessThanOrEqualTo(2000);
        r.Should().Contain("[400 linhas; mostrando");

        string grupos = Q(Pasta(g), """{"group":"Nome","limit":500}""", teto: 1500);
        grupos.Length.Should().BeLessThanOrEqualTo(1500);
        grupos.Should().Contain("TOTAL\t400");
    }

    [Fact]
    public void SemCabecalho_Erro()
    {
        var g = new Grade("Vazia");
        for (int i = 0; i < 6; i++) g.Acrescentar(new[] { C("") });
        g.Acrescentar(new[] { C("tarde demais") });
        var acao = () => Q(Pasta(g), "{}");
        acao.Should().Throw<ErroDeDocumento>().Which.Message.Should().Contain("cabeçalho");
    }

    private static Grade Montar(string[] cabecalho, params Celula[][] linhas)
    {
        var g = new Grade("Dados");
        g.Acrescentar(cabecalho.Select(C).ToArray());
        foreach (var l in linhas) g.Acrescentar(l);
        return g;
    }
}
