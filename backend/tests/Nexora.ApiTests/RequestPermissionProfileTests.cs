using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nexora.Api.Security;
using Nexora.Application.Abstractions;

namespace Nexora.ApiTests;

/// <summary>Audit 2026-10, P-001 : le profil de permissions est chargé une fois par requête dans le pipeline.</summary>
public sealed class RequestPermissionProfileTests
{
    private sealed class CountingPermissions : IPermissionService
    {
        public int Loads { get; private set; }
        public Task<UserSecurityProfile> LoadProfileAsync(Guid userId, CancellationToken ct = default)
        {
            Loads++;
            return Task.FromResult(new UserSecurityProfile(userId, []));
        }
    }

    [Fact]
    public async Task Successive_checks_of_one_request_load_the_profile_once()
    {
        var permissions = new CountingPermissions();
        var http = new DefaultHttpContext();
        var user = Guid.NewGuid();

        await RequestPermissionProfile.GetAsync(http, permissions, user);
        await RequestPermissionProfile.GetAsync(http, permissions, user);
        permissions.Loads.Should().Be(1);

        // Autre requête, ou autre utilisateur : chargement à nouveau.
        await RequestPermissionProfile.GetAsync(new DefaultHttpContext(), permissions, user);
        await RequestPermissionProfile.GetAsync(http, permissions, Guid.NewGuid());
        permissions.Loads.Should().Be(3);
    }
}
