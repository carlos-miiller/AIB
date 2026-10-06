using System;
using System.Windows;
using Microsoft.Win32;

namespace AIB.Ui;

/// <summary>
/// O tema de cores do AIB: claro ou escuro, escolhido nas configurações ou seguindo o Windows.
/// <para>
/// Cada tema é um dicionário com as MESMAS chaves (<c>Themes/Cores.Escuro.xaml</c> e
/// <c>Themes/Cores.Claro.xaml</c>). Ele entra nos recursos do app ANTES do
/// <c>Controls.xaml</c>, e todo <c>StaticResource</c> de cor — dos estilos e das telas — resolve
/// contra ele.
/// </para>
/// <para>
/// Vale a partir do próximo arranque. Os recursos são <c>StaticResource</c>, congelados na
/// carga: trocar ao vivo exigiria converter centenas de referências e toda bolha já desenhada
/// por código, para uma escolha que se faz uma vez.
/// </para>
/// </summary>
public static class Tema
{
    public const string Escuro = "Escuro";
    public const string Claro = "Claro";
    public const string Sistema = "Sistema";

    /// <summary>As opções do seletor, na ordem em que aparecem.</summary>
    public static readonly string[] Opcoes = [Escuro, Claro, Sistema];

    /// <summary>
    /// O tema que vale de fato. "Sistema" pergunta ao Windows; escolha desconhecida (arquivo
    /// antigo, valor corrompido) cai no escuro, que é o tema original.
    /// </summary>
    /// <param name="windowsUsaClaro">O que o Windows diz, ou null se não disser.</param>
    public static string Efetivo(string? escolha, bool? windowsUsaClaro) => Normalizar(escolha) switch
    {
        Claro => Claro,
        Sistema => windowsUsaClaro == true ? Claro : Escuro,
        _ => Escuro
    };

    /// <summary>A escolha gravada, numa das <see cref="Opcoes"/>; desconhecida vira escuro.</summary>
    public static string Normalizar(string? escolha)
    {
        foreach (var opcao in Opcoes)
            if (string.Equals(escolha, opcao, StringComparison.OrdinalIgnoreCase)) return opcao;
        return Escuro;
    }

    /// <summary>
    /// Se os APLICATIVOS do Windows estão no modo claro (Configurações → Personalização →
    /// Cores). Null quando a chave não existe (Windows antigo) ou não pode ser lida.
    /// </summary>
    public static bool? WindowsUsaClaro()
    {
        try
        {
            using var chave = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return chave?.GetValue("AppsUseLightTheme") is int valor ? valor != 0 : null;
        }
        catch
        {
            return null;
        }
    }

    public static Uri UriDasCores(string temaEfetivo) =>
        new($"pack://application:,,,/AIB;component/Themes/Cores.{temaEfetivo}.xaml");

    /// <summary>
    /// Propriedade, e não campo estático: o esquema <c>pack://</c> só existe depois que o WPF
    /// sobe. Como campo, ele era criado no inicializador da classe, e qualquer uso de
    /// <see cref="Efetivo"/> antes do WPF (um teste isolado) derrubava a classe inteira com
    /// "Invalid port specified".
    /// </summary>
    public static Uri UriDosControles =>
        new("pack://application:,,,/AIB;component/Themes/Controls.xaml");

    /// <summary>
    /// Põe o tema nos recursos: as cores primeiro, os estilos depois. A ordem é o que faz os
    /// estilos acharem as cores. Chamado uma vez, antes de qualquer janela.
    /// </summary>
    public static void Carregar(ResourceDictionary recursos, string temaEfetivo)
    {
        recursos.MergedDictionaries.Clear();
        recursos.MergedDictionaries.Add(new ResourceDictionary { Source = UriDasCores(temaEfetivo) });
        recursos.MergedDictionaries.Add(new ResourceDictionary { Source = UriDosControles });
    }
}
