//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------
namespace Aspire.Azure.Search.Documents
{
    public sealed partial class AzureSearchSettings
    {
        public global::Azure.Core.TokenCredential? Credential { get { throw null; } set { } }

        public bool DisableHealthChecks { get { throw null; } set { } }

        public bool DisableTracing { get { throw null; } set { } }

        public System.Uri? Endpoint { get { throw null; } set { } }

        public string? Key { get { throw null; } set { } }
    }
}

namespace Microsoft.Extensions.Hosting
{
    public static partial class AspireAzureSearchExtensions
    {
        public static void AddAzureSearchClient(this IHostApplicationBuilder builder, string connectionName, System.Action<Aspire.Azure.Search.Documents.AzureSearchSettings>? configureSettings = null, System.Action<global::Azure.Core.Extensions.IAzureClientBuilder<global::Azure.Search.Documents.Indexes.SearchIndexClient, global::Azure.Search.Documents.SearchClientOptions>>? configureClientBuilder = null) { }

        public static void AddKeyedAzureSearchClient(this IHostApplicationBuilder builder, string name, System.Action<Aspire.Azure.Search.Documents.AzureSearchSettings>? configureSettings = null, System.Action<global::Azure.Core.Extensions.IAzureClientBuilder<global::Azure.Search.Documents.Indexes.SearchIndexClient, global::Azure.Search.Documents.SearchClientOptions>>? configureClientBuilder = null) { }
    }
}