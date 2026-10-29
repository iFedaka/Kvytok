using Aspire.Hosting.Azure;

internal static class DatabaseRoles
{
    /// <summary>
    /// Tells the migrator which database users to narrow from db_owner to reading and writing rows:
    /// those of the identities Aspire made for <paramref name="apps"/>. Aspire's role script makes
    /// every identity db_owner and cannot be configured, so the migrator, itself db_owner, takes it
    /// back after each deploy. Only in Azure: locally there are no identities and no users to narrow.
    /// </summary>
    public static IResourceBuilder<ProjectResource> NarrowsToReadWrite(
        this IResourceBuilder<ProjectResource> migrator, params IResourceBuilder<ProjectResource>[] apps) =>
        migrator.WithEnvironment(context =>
        {
            if (!context.ExecutionContext.IsPublishMode)
            {
                return;
            }

            for (var i = 0; i < apps.Length; i++)
            {
                // Aspire also calls this while discovering dependencies, before it has given the apps
                // their identities; the call that writes the Bicep comes after.
                if (!apps[i].Resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var identity))
                {
                    return;
                }

                // The database user Aspire created is named after the identity
                context.EnvironmentVariables[$"Database__ReadWriteUsers__{i}"] = identity.IdentityResource.PrincipalName;
            }
        });
}
