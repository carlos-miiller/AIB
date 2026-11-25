using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests;

/// <summary>
/// O corretor ortográfico da caixa de mensagem. Pedido do usuário (24/11/2026): sublinhar a
/// palavra errada e, no botão direito, mostrar as sugestões e "Adicionar ao dicionário" — o menu
/// padrão do WPF não tem o "adicionar", e o dicionário do usuário mora em ~/.AIB.
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

    private const BindingFlags Privados = BindingFlags.Instance | BindingFlags.NonPublic;

    private static TextBox Caixa(ChatWindow w) => (TextBox)w.FindName("InputBox");

    private static void Chamar(ChatWindow w, string metodo, params object?[] args) =>
        typeof(ChatWindow).GetMethod(metodo, Privados)!.Invoke(w, args);

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
    public void Adicionar_GravaAPalavraERegistraODicionario_SemRepetir()
    {
        string lex = Path.Combine(_pasta, "dicionario.lex");
        WpfHost.EmSta(() =>
        {
            WpfHost.GarantirRecursos();
            var w = JanelaDeEnsaio.Nova();
            typeof(ChatWindow).GetProperty("Dicionario", Privados)!.SetValue(w, lex);

            Chamar(w, "AdicionarAoDicionario", "Bitrix");
            Chamar(w, "AdicionarAoDicionario", "Bitrix");
            Chamar(w, "AdicionarAoDicionario", "duas palavras");

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
            Chamar(w, "MontarMenu", null, "");

            Caixa(w).ContextMenu!.Items.OfType<MenuItem>().Select(m => (string)m.Header)
                .Should().Equal("Recortar", "Copiar", "Colar");
        });
    }

    /// <summary>
    /// O caminho inteiro com o corretor do Windows: "caza" sublinhada, com "casa" entre as
    /// sugestões, e sem sublinhado logo depois de adicionada. Sem o corretor pt-BR instalado no
    /// Windows não há o que medir, e o teste só confere o que der.
    /// </summary>
    [Fact]
    public void Adicionar_TiraOSublinhadoNaHora()
    {
        WpfHost.EmSta(() =>
        {
            WpfHost.GarantirRecursos();
            var w = JanelaDeEnsaio.Nova();
            typeof(ChatWindow).GetProperty("Dicionario", Privados)!.SetValue(w, Path.Combine(_pasta, "d.lex"));
            var c = Caixa(w);
            w.Show();
            try
            {
                c.Text = "eu quero uma caza bonita";
                c.UpdateLayout();
                var erro = c.GetSpellingError(14);
                if (erro == null) return;
                erro.Suggestions.Should().Contain("casa");

                Chamar(w, "AdicionarAoDicionario", "caza");
                c.UpdateLayout();
                c.GetSpellingError(14).Should().BeNull();
            }
            finally { w.Close(); }
        });
    }
}
