namespace WattScope.Core.Classification;

/// <summary>
/// How a candidate's role was determined. Recorded explicitly so consumers
/// can tell a metadata-backed classification (provider hardware type +
/// sensor type) from one that additionally needed the sensor's display name
/// to disambiguate, since no monitoring provider used here exposes a role
/// enum stronger than name + index for that last step.
/// </summary>
public enum ClassificationSource
{
    /// <summary>Hardware category and provider sensor-type alone were sufficient.</summary>
    ProviderMetadata,

    /// <summary>Matched against a fixed, documented table of known provider-native sensor names/indices.</summary>
    KnownNamePattern,

    /// <summary>No known mapping matched; role is <see cref="PowerSensorRole.Other"/>.</summary>
    Unclassified
}
