namespace LabWeb.Bun
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Web;

    public class BunStore
    {
        public static BunStore Instance = new BunStore();

        private ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private Dictionary<string, Blob> store = new Dictionary<string, Blob>();
        
        public Blob Store(string blobPath, Blob blob)
        {
            _lock.EnterWriteLock();

            try
            {
                store.Add(blobPath, blob);

                return blob;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public Blob Resolve(string blobPath)
        {
            _lock.EnterReadLock();

            try
            {
                Blob result;
                store.TryGetValue(blobPath, out result);
                return result;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public HtmlString IncludeBlob(string blobPath)
        {
            var blob = this.Resolve(blobPath);
            //Debug.Assert(blob.MimeType == FileBlob.MimeHtml); // TEMP

            return new HtmlString(blob.ToString());
        }
    }
}