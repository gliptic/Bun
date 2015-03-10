namespace LabWeb.Bun
{
    public interface IBlobResolver
    {
        Blob GetFile(string virtualPath);
        Blob GetTransformedFile(string virtualPath);

        // TODO: 'cd' function to change the base path
    }
}