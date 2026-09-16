using System;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink
{
    /// <summary>
    /// Advanced API. A single lease on a content-directory asset.
    /// Prefer <see cref="ContentScope"/> / <see cref="ContentLink{T}"/>: a raw handle that is never disposed
    /// leaks until shutdown. Hand it to a scope with <see cref="ContentScope.Attach{T}"/> to make it safe.
    ///
    /// Copies share the same lease (no AddRef). Double Dispose is detected by generation and ignored.
    /// Use <see cref="Share"/> to take an additional reference.
    /// </summary>
    public readonly struct AssetHandle<T> : IDisposable, IEquatable<AssetHandle<T>> where T : Object
    {
        internal readonly int Lease;
        internal readonly uint Gen;   // 0 = invalid (default)

        internal AssetHandle(int lease, uint gen) { Lease = lease; Gen = gen; }

        public bool IsValid => ContentService.IsLeaseAlive(Lease, Gen);
        public T Asset => ContentService.GetAsset<T>(Lease, Gen);
        public string Key => ContentService.GetLeaseKey(Lease, Gen);
        public ContentStatus Status => ContentService.GetLeaseStatus(Lease, Gen);

        public AssetHandle<T> Share() => ContentService.Share<T>(Lease, Gen);
        public void Dispose() => ContentService.Release(Lease, Gen);

        internal AssetHandle<U> As<U>() where U : Object => new AssetHandle<U>(Lease, Gen);

        public bool Equals(AssetHandle<T> other) => Lease == other.Lease && Gen == other.Gen;
        public override bool Equals(object obj) => obj is AssetHandle<T> o && Equals(o);
        public override int GetHashCode() => (Lease * 397) ^ (int)Gen;
        public override string ToString() => IsValid ? $"AssetHandle<{typeof(T).Name}>({Key})" : "AssetHandle(invalid)";
    }
}
