namespace WgbDiagnostics.Core.Wgb;

public static class WgbRoamClassifier
{
    public static WgbRoamClassification Classify(
        WgbAssociationSnapshot oldAssociation,
        WgbAssociationSnapshot newAssociation)
    {
        var parentChanged = HasChanged(oldAssociation.ParentApName, newAssociation.ParentApName);
        if (parentChanged)
        {
            if (HasComparableValue(oldAssociation.Channel, newAssociation.Channel))
            {
                return StringComparer.OrdinalIgnoreCase.Equals(oldAssociation.Channel, newAssociation.Channel)
                    ? WgbRoamClassification.DifferentApSameChannel
                    : WgbRoamClassification.DifferentApDifferentChannel;
            }

            return WgbRoamClassification.Unknown;
        }

        if (HasComparableValue(oldAssociation.ParentApName, newAssociation.ParentApName) &&
            StringComparer.OrdinalIgnoreCase.Equals(oldAssociation.ParentApName, newAssociation.ParentApName) &&
            HasChanged(oldAssociation.RadioId, newAssociation.RadioId))
        {
            return WgbRoamClassification.SameApDifferentRadio;
        }

        return WgbRoamClassification.Unknown;
    }

    private static bool HasChanged(string? oldValue, string? newValue)
    {
        return HasComparableValue(oldValue, newValue) &&
            !StringComparer.OrdinalIgnoreCase.Equals(oldValue, newValue);
    }

    private static bool HasComparableValue(string? oldValue, string? newValue)
    {
        return !string.IsNullOrWhiteSpace(oldValue) && !string.IsNullOrWhiteSpace(newValue);
    }
}
