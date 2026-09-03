using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using AIB.Services.Mail;

namespace AIB.Ui;

/// <summary>
/// Traduz o estado de uma caixa de e-mail para o que a linha de §3.12 precisa desenhar.
/// <para>
/// Um conversor com modo, e não três classes, pelo mesmo motivo do
/// <see cref="UrgenciaConverter"/>: a cor do ponto e o texto do ToolTip descrevem o MESMO
/// estado, e separá-los abre a porta para eles discordarem — ponto verde com ToolTip
/// "Falha de login" é o tipo de defeito que ninguém percebe até um usuário reclamar.
/// </para>
/// </summary>
public sealed class ContaDeEmailConverter : IValueConverter
{
    public enum Saida
    {
        /// <summary>Cor do ponto de estado da coluna 0.</summary>
        CorDoPonto,

        /// <summary>ToolTip do ponto, verbatim de §3.12.</summary>
        RotuloDoPonto,

        /// <summary>Pincel do texto de estado: cinza normalmente, vermelho em erro.</summary>
        CorDoTexto,

        /// <summary>Visible quando é a conta principal — o selo.</summary>
        VisibilidadeDoSelo,

        /// <summary>Visible quando NÃO é a principal — o botão de estrela.</summary>
        VisibilidadeDaEstrela
    }

    public Saida Modo { get; set; } = Saida.CorDoPonto;

    // Congelados: são criados uma vez e usados em toda linha da lista.
    private static readonly SolidColorBrush Verde = Congelar(0x3F, 0xBF, 0x7F);
    private static readonly SolidColorBrush Ambar = Congelar(0xE2, 0xA0, 0x3F);
    private static readonly SolidColorBrush Vermelho = Congelar(0xE5, 0x48, 0x4D);
    private static readonly SolidColorBrush Cinza = Congelar(0x6F, 0x6A, 0x7A);
    private static readonly SolidColorBrush VermelhoDeTexto = Congelar(0xFF, 0x8A, 0x8D);

    private static SolidColorBrush Congelar(byte r, byte g, byte b)
    {
        var pincel = new SolidColorBrush(Color.FromRgb(r, g, b));
        pincel.Freeze();
        return pincel;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (Modo is Saida.VisibilidadeDoSelo or Saida.VisibilidadeDaEstrela)
        {
            bool principal = value is bool b && b;
            bool mostrar = Modo == Saida.VisibilidadeDoSelo ? principal : !principal;
            return mostrar ? Visibility.Visible : Visibility.Collapsed;
        }

        var estado = value is MailAccountStatus s ? s : MailAccountStatus.Checking;

        return Modo switch
        {
            Saida.CorDoPonto => estado switch
            {
                MailAccountStatus.Ok => Verde,
                MailAccountStatus.Error => Vermelho,
                _ => Ambar
            },

            Saida.RotuloDoPonto => estado switch
            {
                MailAccountStatus.Ok => "Conectada",
                MailAccountStatus.Error => "Falha de login",
                _ => "Verificando…"
            },

            Saida.CorDoTexto => estado == MailAccountStatus.Error ? VermelhoDeTexto : Cinza,

            _ => Cinza
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("mão única: o estado da conta vem do serviço, não da tela");
}
