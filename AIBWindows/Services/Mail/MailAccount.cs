using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIB.Services.Mail;

/// <summary>Estado de uma conta na lista — §3.12, ponto de cor da coluna 0.</summary>
public enum MailAccountStatus
{
    /// <summary>Verde. Login IMAP passou e a última leitura está registrada.</summary>
    Ok,

    /// <summary>Âmbar. Verificação em curso — ou pendente, enquanto não há serviço IMAP.</summary>
    Checking,

    /// <summary>Vermelho. Falha de login; o texto de estado carrega o motivo.</summary>
    Error
}

/// <summary>
/// Uma caixa de e-mail configurada — §3.12 e §9 passo 1 de tela-configuracoes.
/// <para>
/// A SENHA NÃO É PROPRIEDADE DESTA CLASSE, de propósito. Ela mora no
/// <see cref="MailVault"/>, indexada pelo endereço, justamente para não poder cair num
/// binding, num log ou numa serialização por descuido. O que se guarda aqui é
/// <see cref="HasPassword"/>: existe blob no cofre, sim ou não.
/// </para>
/// </summary>
public sealed class MailAccount : INotifyPropertyChanged
{
    private string _address = "";
    private string _imapHost = "";
    private int _imapPort = 993;
    private bool _useSsl = true;
    private bool _hasPassword;
    private bool _isPrimary;
    private MailAccountStatus _status = MailAccountStatus.Checking;
    private string _statusText = "";
    private DateTime? _lastReadUtc;

    /// <summary>
    /// Chave da conta. Comparações em <see cref="StringComparer.OrdinalIgnoreCase"/>: o
    /// usuário digita o mesmo endereço com maiúsculas diferentes e espera que seja o mesmo.
    /// </summary>
    public string Address
    {
        get => _address;
        set => Definir(ref _address, value);
    }

    /// <summary>Deduzido do domínio, nunca digitado — ver <see cref="ImapHostGuesser"/>.</summary>
    public string ImapHost
    {
        get => _imapHost;
        set => Definir(ref _imapHost, value);
    }

    public int ImapPort
    {
        get => _imapPort;
        set => Definir(ref _imapPort, value);
    }

    public bool UseSsl
    {
        get => _useSsl;
        set => Definir(ref _useSsl, value);
    }

    /// <summary>Existe blob no cofre para este endereço.</summary>
    public bool HasPassword
    {
        get => _hasPassword;
        set => Definir(ref _hasPassword, value);
    }

    /// <summary>
    /// Conta principal — o selo de §3.12. Quem garante que existe exatamente uma é a
    /// <see cref="MailAccountList"/>, não esta propriedade.
    /// </summary>
    public bool IsPrimary
    {
        get => _isPrimary;
        set => Definir(ref _isPrimary, value);
    }

    public MailAccountStatus Status
    {
        get => _status;
        set => Definir(ref _status, value);
    }

    /// <summary>
    /// Segunda linha do bloco de texto: "host:porta · última leitura HH:mm" quando está bem,
    /// ou a mensagem do servidor em linguagem de usuário quando <see cref="Status"/> é
    /// <see cref="MailAccountStatus.Error"/> — nunca a exceção do cliente IMAP.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        set => Definir(ref _statusText, value);
    }

    public DateTime? LastReadUtc
    {
        get => _lastReadUtc;
        set => Definir(ref _lastReadUtc, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Definir<T>(ref T campo, T valor, [CallerMemberName] string? nome = null)
    {
        if (Equals(campo, valor)) return;
        campo = valor;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nome));
    }
}
