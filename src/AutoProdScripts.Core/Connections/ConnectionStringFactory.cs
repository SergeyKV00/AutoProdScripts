using Microsoft.Data.SqlClient;

namespace AutoProdScripts.Core.Connections;

public static class ConnectionStringFactory
{
    public static string Build(DbConnectionProfile profile, string? password)
    {
        if (string.IsNullOrWhiteSpace(profile.Server))
            throw new InvalidOperationException("Не указан сервер.");

        var builder = new SqlConnectionStringBuilder(profile.ExtraParameters ?? string.Empty)
        {
            DataSource = profile.Server.Trim(),
            InitialCatalog = profile.Database.Trim(),
            Encrypt = profile.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = profile.TrustServerCertificate,
            ConnectTimeout = Math.Max(1, profile.ConnectTimeoutSeconds),
            PersistSecurityInfo = false,
            ApplicationName = "AutoProdScripts"
        };

        if (profile.ReadOnlyIntent)
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        else
            builder.Remove("Application Intent");

        if (profile.AuthMode == DbAuthMode.Windows)
        {
            builder.IntegratedSecurity = true;
            builder.Remove("User ID");
            builder.Remove("Password");
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = profile.UserName;
            builder.Password = password ?? string.Empty;
        }

        return builder.ConnectionString;
    }
}
