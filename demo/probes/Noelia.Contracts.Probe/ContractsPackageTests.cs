using System.ComponentModel.DataAnnotations;
using Noelia.Contracts;

namespace Noelia.Contracts.Probe;

public sealed class ContractsPackageTests
{
    [Fact]
    public void Paged_contract_exposes_stable_wire_semantics()
    {
        var request = new PagedRequest(PageNumber: 3, PageSize: 20);

        request.ApiVersion.Should().Be("v1");
        request.Skip.Should().Be(40);
        request.Take.Should().Be(20);
    }

    [Fact]
    public void Invalid_page_size_is_rejected_by_the_public_contract()
    {
        var request = new PagedRequest(PageSize: 101);
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(request, new ValidationContext(request), results, true)
            .Should().BeFalse();
        results.Should().ContainSingle(result => result.MemberNames.Contains("PageSize"));
    }
}
