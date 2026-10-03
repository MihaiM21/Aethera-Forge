namespace Aethera.Domain;

/// <summary>Thrown when a domain invariant or state-transition guard is violated.</summary>
public sealed class DomainRuleException(string message) : InvalidOperationException(message);
