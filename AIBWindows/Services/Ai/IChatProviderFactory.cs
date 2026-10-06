using AIB.Services;

namespace AIB.Services.Ai;

/// <summary>Fábrica de providers dirigida pelas settings do usuário.</summary>
public interface IChatProviderFactory
{
    /// <summary>
    /// Devolve o provider correspondente às settings atuais. Reaproveita a instância
    /// anterior enquanto provider, modelo, URL e credencial efetiva não mudarem — a
    /// credencial entra na comparação porque trocar a chave no cofre não muda nada nas
    /// configurações. Deve ser thread-safe: o aquecimento e um turno
    /// podem correr juntos na inicialização.
    /// </summary>
    IChatProvider GetProvider(UserAppSettings settings);
}
