using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// O corretor ortográfico das caixas de texto. Pedido do usuário (24/11/2026): sublinhar a
/// palavra errada e, no botão direito, mostrar as sugestões e "Adicionar ao dicionário" — o menu
/// padrão do WPF não tem o "adicionar", e o dicionário do usuário mora em ~/.AIB. Em 26/11: o
/// orbe tinha ficado sem.
/// </summary>
[Collection("ContextoGlobal")]
public class OrtografiaDaCaixaTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "aib-orto-" + Guid.NewGuid().ToString("N"));

    public OrtografiaDaCaixaTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, true); } catch { }
    }

    private static TextBox Caixa(ChatWindow w) => (TextBox)w.FindName("InputBox");

    [Fact]
    public void Caixa_CorrigeEmPortugues()
    {
        WpfHost.EmSta(() =>
        {
            WpfHost.GarantirRecursos();
            var w = JanelaDeEnsaio.Nova();
            SpellCheck.GetIsEnabled(Caixa(w)).Should().BeTrue();
            Caixa(w).Language.IetfLanguageTag.Should().BeEquivalentTo("pt-BR");
        });
    }

    [Fact]
    public void Orbe_TemOMesmoCorretor()
    {
        WpfHost.EmSta(() =>
        {
            WpfHost.GarantirRecursos();
            var orbe = new ShadowAssistantWindow();
            var campo = (TextBox)orbe.FindName("Campo");

            SpellCheck.GetIsEnabled(campo).Should().BeTrue("o orbe tinha ficado sem o corretor");
            campo.Language.IetfLanguageTag.Should().BeEquivalentTo("pt-BR");
            campo.ContextMenu.Should().NotBeNull("sem ele o TextBox abre o menu do WPF, sem o 'adicionar'");
        });
    }

    [Fact]
    public void Adicionar_GravaAPalavraERegistraODicionario_SemRepetir()
    {
        string lex = Path.Combine(_pasta, "dicionario.lex");
        WpfHost.EmSta(() =>
        {
            WpfHost.GarantirRecursos();
            var w = JanelaDeEnsaio.Nova();
            w.Ortografia.Dicionario = lex;

            w.Ortografia.Adicionar("Bitrix");
            w.Ortografia.Adicionar("Bitrix");
            w.Ortografia.Adicionar("duas palavras");

            File.ReadAllLines(lex, Encoding.Unicode).Should().Equal("Bitrix");
            SpellCheck.GetCustomDictionaries(Caixa(w))
                .OfType<Uri>().Should().ContainSingle(u => u.LocalPath == Path.GetFullPath(lex));
        });
    }

    [Fact]
    public void Menu_SemErroNoPonto_SoRecortarCopiarColar()
    {
        WpfHost.EmSta(() =>
        {
            WpfHost.GarantirRecursos();
            var w = JanelaDeEnsaio.Nova();
            w.Ortografia.MontarMenu(null, "");

            Caixa(w).ContextMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header)
                .Should().Equal("Recortar", "Copiar", "Colar");
        });
    }

    /// <summary>
    /// O caminho inteiro com o corretor do Windows: "caza" sublinhada, com "casa" entre as
    /// sugestões, e sem sublinhado logo depois de adicionada — também na caixa do orbe, que
    /// divide o dicionário. Sem o corretor pt-BR instalado no Windows não há o que medir.
    /// </summary>
    [Fact]
    public void Adicionar_TiraOSublinhadoNaHora_NasDuasCaixas()
    {
        string lex = Path.Combine(_pasta, "d.lex");
        WpfHost.EmSta(() =>
        {
            WpfHost.GarantirRecursos();
            var w = JanelaDeEnsaio.Nova();
            var orbe = new ShadowAssistantWindow();
            w.Ortografia.Dicionario = lex;
            orbe.Ortografia.Dicionario = lex;
            var c = Caixa(w);
            var campo = (TextBox)orbe.FindName("Campo");
            w.Show();
            orbe.Show();
            try
            {
                c.Text = campo.Text = "eu quero uma caza bonita";
                c.UpdateLayout();
                campo.UpdateLayout();
                var erro = c.GetSpellingError(14);
                if (erro == null) return;
                erro.Suggestions.Should().Contain("casa");
                campo.GetSpellingError(14).Should().NotBeNull();

                w.Ortografia.Adicionar("caza");
                c.UpdateLayout();
                campo.UpdateLayout();
                c.GetSpellingError(14).Should().BeNull();
                campo.GetSpellingError(14).Should().BeNull("o orbe usa o mesmo dicionário");
            }
            finally
            {
                w.Close();
                orbe.Close();
            }
        });
    }
}
