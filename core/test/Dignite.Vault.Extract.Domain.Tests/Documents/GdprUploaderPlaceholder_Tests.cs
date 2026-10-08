using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// The placeholder that replaces an erased uploader's name (#698) is stored in a column of
/// <see cref="FileOriginConsts.MaxUploadedByUserNameLength"/> characters and is a value <see cref="FileOrigin"/> itself
/// must accept (its constructor rejects a blank or over-long name). The test database is SQLite, which enforces no
/// column length, so the length is pinned here instead of being left to SQL Server to find.
/// </summary>
public class GdprUploaderPlaceholder_Tests
{
    [Fact]
    public void The_placeholder_is_not_blank()
    {
        FileOriginConsts.AnonymizedUploaderName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_placeholder_fits_the_uploader_name_column()
    {
        FileOriginConsts.AnonymizedUploaderName.Length
            .ShouldBeLessThanOrEqualTo(FileOriginConsts.MaxUploadedByUserNameLength);
    }

    [Fact]
    public void FileOrigin_accepts_the_placeholder_as_a_name()
    {
        // Not the same check as the two above: this runs the constructor's own validation, so it keeps holding if the
        // constructor ever grows a rule (a character set, a stricter length) the constant would then have to meet.
        var origin = new FileOrigin(
            blobName: "blobs/x.pdf",
            uploadedByUserName: FileOriginConsts.AnonymizedUploaderName,
            contentType: "application/pdf",
            contentHash: new string('a', 64),
            fileSize: 1);

        origin.UploadedByUserName.ShouldBe(FileOriginConsts.AnonymizedUploaderName);
    }
}
