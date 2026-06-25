using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Vault.Extract.Documents;

public class UpdateExtractedFieldsInput
{
    /// <summary>
    /// Field values keyed by <see cref="FieldDefinition.Name"/>. Replaces the complete set of
    /// type-bound field values for this document.
    /// The caller submits all current field values for the document; every key must be a field name
    /// defined under the document's own layer and DocumentType.
    /// Values stay as raw JSON with no forced DataType conversion. Consumers deserialize according to
    /// <see cref="FieldDefinition.DataType"/>.
    /// </summary>
    public Dictionary<string, JsonElement> Fields { get; set; } = new();
}
