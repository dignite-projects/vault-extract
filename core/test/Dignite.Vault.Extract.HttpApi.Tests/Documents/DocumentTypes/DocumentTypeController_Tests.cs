using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// HttpApi integration tests for <see cref="HttpApi.Documents.DocumentTypes.DocumentTypeController"/>:
/// drives the REST endpoints through the MVC pipeline (routing, model binding, validation, authorization,
/// serialization, the application service, and EF persistence) over an in-memory SQLite database.
/// </summary>
public class DocumentTypeController_Tests : ExtractHttpApiTestBase
{
    private const string Url = "/api/vault-extract/document-types";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get_Visible_Returns_Empty_List_On_A_Fresh_Database()
    {
        var response = await Client.GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var types = await DeserializeAsync<List<DocumentTypeDto>>(response);
        types.ShouldNotBeNull();
        // No built-in document types: a freshly seeded database has none.
        types!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_Then_List_Roundtrips_Through_The_Rest_Pipeline()
    {
        var input = new CreateDocumentTypeDto
        {
            TypeCode = "invoice",
            DisplayName = "Invoice",
            ConfidenceThreshold = 0.8,
            Priority = 5
        };

        var createResponse = await Client.PostAsJsonAsync(Url, input, Json);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var created = await DeserializeAsync<DocumentTypeDto>(createResponse);
        created.ShouldNotBeNull();
        created!.Id.ShouldNotBe(Guid.Empty);
        created.TypeCode.ShouldBe("invoice");
        created.DisplayName.ShouldBe("Invoice");

        var listResponse = await Client.GetAsync(Url);
        var types = await DeserializeAsync<List<DocumentTypeDto>>(listResponse);
        types.ShouldNotBeNull();
        types!.ShouldContain(t => t.TypeCode == "invoice" && t.Id == created.Id);
    }

    [Fact]
    public async Task Create_With_Out_Of_Range_Confidence_Threshold_Returns_400()
    {
        // ConfidenceThreshold is [Range(0,1)]; ABP model validation must reject it at the HTTP boundary.
        var input = new CreateDocumentTypeDto
        {
            TypeCode = "bad",
            DisplayName = "Bad",
            ConfidenceThreshold = 5
        };

        var response = await Client.PostAsJsonAsync(Url, input, Json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<T?> DeserializeAsync<T>(System.Net.Http.HttpResponseMessage response)
        => JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), Json);
}
