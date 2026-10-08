using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIB.Services;

/// <summary>
/// Texto gravado cifrado pelo DPAPI do usuário do Windows, como o <c>profile.dat</c> das
/// configurações: só a conta que gravou lê, e a cópia do arquivo para outra máquina ou outro
/// usuário não abre.
/// <para>
/// Não protege de um programa rodando na mesma conta — esse pede a mesma chave ao Windows. O
/// que sai do alcance é o disco copiado, o backup e o outro usuário da máquina.
/// </para>
/// </summary>
public static class ArquivoCifrado
{
    /// <summary>
    /// O texto do arquivo, ou null quando ele não existe. Arquivo em texto claro — o de antes
    /// da cifra — é lido como está: quem chama regrava, e ele passa a ser cifrado.
    /// </summary>
    public static string? Ler(string caminho)
    {
        if (!File.Exists(caminho)) return null;

        byte[] bytes = File.ReadAllBytes(caminho);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        }
    }

    public static void Gravar(string caminho, string texto)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllBytes(caminho, ProtectedData.Protect(Encoding.UTF8.GetBytes(texto), null, DataProtectionScope.CurrentUser));
    }
}
