﻿using AresEditor;
using Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using AresEditor;

static class ListExtra {

    public static void Resize<T>( this List<T> list, int sz, T c ) {
        int cur = list.Count;
        if ( sz < cur ) {
            list.RemoveRange( sz, cur - sz );
        } else if ( sz > cur ) {
            //this bit is purely an optimisation, to avoid multiple automatic capacity changes.
            if ( sz > list.Capacity ) {
                list.Capacity = sz;
            }
            int count = sz - cur;
            for ( int i = 0; i < count; ++i ) {
                list.Add( c );
            }
        }
    }

    public static void Resize<T>( this List<T> list, int sz ) {
        Resize( list, sz, default( T ) );
    }
}

unsafe static class MathLib {

    public static int CeilPowerOfTwo( int x ) {
        x--;
        x |= x >> 1;
        x |= x >> 2;
        x |= x >> 4;
        x |= x >> 8;
        x |= x >> 16;
        x++;
        return x;
    }

    public static float ClampRepeatUV( float value ) {
        if ( Mathf.Abs( value ) < 1e-6f ) {
            value = 0;
        }
        if ( Mathf.Abs( 1 - value ) < 1e-6f ) {
            value = 1;
        }
        if ( value < 0 || value > 1 ) {
            var _value = value % 1.0f;
            if ( _value < 0.0 ) {
                _value += 1.0f;
            }
            return _value;
        }
        return value;
    }

    public static float ClampRepeatUV( float value, ref bool modified ) {
        if ( Mathf.Abs( value ) < 1e-6f ) {
            value = 0;
        }
        if ( Mathf.Abs( 1 - value ) < 1e-6f ) {
            value = 1;
        }
        if ( value < 0 || value > 1 ) {
            var _value = value % 1.0f;
            if ( _value < 0.0 ) {
                _value += 1.0f;
            }
            modified = _value != value;
            return _value;
        } else {
            modified = false;
            return value;
        }
    }

    public static Vector4 CalculateUVScaleOffset( Vector4 meshUVBounds, Vector4 uvBounds ) {
        var scaleOffset = Vector4.zero;
        scaleOffset.x = ( uvBounds.z - uvBounds.x ) / ( meshUVBounds.z - meshUVBounds.x );
        scaleOffset.y = ( uvBounds.w - uvBounds.y ) / ( meshUVBounds.w - meshUVBounds.y );
        scaleOffset.z = uvBounds.z - meshUVBounds.z * scaleOffset.x;
        scaleOffset.w = uvBounds.w - meshUVBounds.w * scaleOffset.y;
        return scaleOffset;
    }

    public static Vector4 ToLightmapUVBounds( Renderer r, Vector4 meshUVBounds ) {
        var lightmapScaleOffset = r.lightmapScaleOffset;
        var lightmapUVBounds = Vector4.zero;
        lightmapUVBounds.x = meshUVBounds.x * lightmapScaleOffset.x + lightmapScaleOffset.z;
        lightmapUVBounds.y = meshUVBounds.y * lightmapScaleOffset.y + lightmapScaleOffset.w;
        lightmapUVBounds.z = meshUVBounds.z * lightmapScaleOffset.x + lightmapScaleOffset.z;
        lightmapUVBounds.w = meshUVBounds.w * lightmapScaleOffset.y + lightmapScaleOffset.w;
        return lightmapUVBounds;
    }
}

static class UnityUtils {
    public static String GetHierarchyPath( Transform trans ) {
        var sb = StringUtils.newStringBuilder;
        sb.Append( trans.name );
        var p = trans.parent;
        while ( p != null ) {
            sb.Insert( 0, '/' );
            sb.Insert( 0, p.name );
            p = p.parent;
        }
        return sb.ToString();
    }
}

class TexturePacker : IDisposable {

    const int MaxPage = 128;

    unsafe class _PagePacker : IDisposable {

        const int MaxSize = 1024;

        int m_atlasSize = 0;
        int m_usedWidth = 0;
        int m_usedHeight = 0;
        int m_usedArea = 0;
        NativeAPI.stbrp_context m_ctx;
        List<NativeAPI.stbrp_rect> m_packedRects = new List<NativeAPI.stbrp_rect>();
        NativeAPI.stbrp_node[] m_pool = null;
        GCHandle m_poolHandle;

        public _PagePacker( int size = 32, int maxSize = MaxSize ) {
            size = MathLib.CeilPowerOfTwo( size );
            if ( size > MaxSize ) {
                size = MaxSize;
            }
            Realloc( size );
        }

        public int atlasSize {
            get {
                return m_atlasSize;
            }
        }

        public bool IsGrowable() {
            return m_atlasSize < MaxSize;
        }

        public bool IsAlmostFull() {
            return GetUseRate() > 0.95f;
        }

        public float GetUseRate() {
            return ( float )m_usedArea / ( m_atlasSize * m_atlasSize );
        }

        public void Realloc( int maxSize ) {
            maxSize = MathLib.CeilPowerOfTwo( maxSize );
            if ( maxSize != m_atlasSize ) {
                if ( m_pool != null ) {
                    m_poolHandle.Free();
                }
                m_usedWidth = 0;
                m_usedHeight = 0;
                m_usedArea = 0;
                m_atlasSize = maxSize;
                m_pool = new NativeAPI.stbrp_node[ m_atlasSize * 4 ];
                UDebug.Assert( m_pool != null && m_pool.Length > 0 );
                m_poolHandle = GCHandle.Alloc( m_pool, GCHandleType.Pinned );
                var packedRects = m_packedRects;
                m_packedRects = new List<NativeAPI.stbrp_rect>();
                m_ctx = new NativeAPI.stbrp_context();
                fixed ( NativeAPI.stbrp_context* _ctx = &m_ctx ) {
                    NativeAPI.stbrp_init_target( _ctx, m_atlasSize, m_atlasSize, ( NativeAPI.stbrp_node* )m_poolHandle.AddrOfPinnedObject(), m_pool.Length );
                    NativeAPI.stbrp_setup_allow_out_of_mem( _ctx, 1 );
                }
                for ( int i = 0; i < packedRects.Count; ++i ) {
                    var rect = packedRects[ i ];
                    var _rect = new NativeAPI.stbrp_rect();
                    _rect.w = rect.w;
                    _rect.h = rect.h;
                    _rect.id = rect.id;
                    if ( !Add( rect ) ) {
                        UDebug.LogError( "Realloc failed!" );
                        break;
                    }
                }
            }
        }

        public void Dispose() {
            if ( m_pool != null ) {
                m_poolHandle.Free();
            }
            m_pool = null;
            m_packedRects = null;
            GC.SuppressFinalize( this );
        }

        public bool Add( NativeAPI.stbrp_rect rect ) {
            if ( rect.w > m_atlasSize || rect.h > m_atlasSize ) {
                if ( IsGrowable() ) {
                    Realloc( Mathf.Max( rect.w, rect.h ) );
                } else {
                    throw new Exception();
                }
            }
            var requireArea = rect.w * rect.h;
            var totalArea = m_atlasSize * m_atlasSize;
            if ( totalArea - m_usedArea - requireArea <= 0 ) {
                return false;
            }
        RETRY:
            fixed ( NativeAPI.stbrp_context* ctx_ = &m_ctx ) {
                NativeAPI.stbrp_pack_rects( ctx_, &rect, 1 );
            }
            if ( rect.was_packed != 0 ) {
                var maxx = rect.x + rect.w;
                var maxy = rect.y + rect.h;
                if ( maxx > m_usedWidth ) {
                    m_usedWidth = maxx;
                }
                if ( maxy > m_usedHeight ) {
                    m_usedHeight = maxy;
                }
                m_usedArea += requireArea;
                m_packedRects.Add( rect );
                return true;
            } else {
                if ( IsGrowable() ) {
                    Realloc( m_atlasSize << 1 );
                    goto RETRY;
                }
            }
            return false;
        }

        public void ForEach( Action<int, int, NativeAPI.stbrp_rect> func, int index = 0 ) {
            for ( int i = 0; i < m_packedRects.Count; ++i ) {
                var tp = m_packedRects[ i ];
                func( index, atlasSize, tp );
            }
        }
    }

    List<_PagePacker> m_pagePacker = new List<_PagePacker>();
    public void Dispose() {
        if ( m_pagePacker != null ) {
            for ( int i = 0; i < m_pagePacker.Count; ++i ) {
                m_pagePacker[ i ].Dispose();
            }
            m_pagePacker = null;
            GC.SuppressFinalize( this );
        }
    }

    public TexturePacker() {
        m_pagePacker.Add( new _PagePacker() );
    }

    public void ForEach( Action<int, int, NativeAPI.stbrp_rect> func ) {
        for ( int i = 0; i < m_pagePacker.Count; ++i ) {
            var tp = m_pagePacker[ i ];
            tp.ForEach( func, i );
        }
    }

    public int PackRects( NativeAPI.stbrp_rect[] rects ) {
        var addedCount = 0;
        var input = new List<NativeAPI.stbrp_rect>( rects );
        var next = new List<NativeAPI.stbrp_rect>();
        while ( input.Count > 0 ) {
            input.Sort(
                ( l, r ) => {
                    if ( r.h > l.h ) {
                        return -1;
                    }
                    if ( r.h < l.h ) {
                        return 1;
                    }
                    return ( r.w > l.w ) ? -1 : ( ( r.w < l.w ) ? 1 : 0 );
                }
            );
            while ( input.Count > 0 ) {
                var last = input.Count - 1;
                var cur = input[ last ];
                input.RemoveAt( last );
                var added = false;
                for ( int i = 0; i < m_pagePacker.Count; ++i ) {
                    var tp = m_pagePacker[ i ];
                    if ( tp.Add( cur ) ) {
                        ++addedCount;
                        added = true;
                        break;
                    }
                }
                if ( !added ) {
                    next.Add( cur );
                }
            }
            if ( next.Count > 0 ) {
                if ( m_pagePacker.Count > MaxPage ) {
                    UDebug.LogError( "There is too many rects to pack: MaxPage = {0}", MaxPage );
                    break;
                }
                var temp = next;
                next = input;
                input = temp;
                m_pagePacker.Add( new _PagePacker() );
            }
        }
        return addedCount;
    }
}

static class LightmapRepacker {

    const String Title = "LightmapRepacker";
    const int Default_TileSize = 32;

    class RendererInfo {
        public Renderer renderer;
        public Bounds bounds; // 渲染器包围盒
        public Mesh mesh;
        public Material[] materials;
        public float bounds_radius = 0f; // 渲染器包球半径
        public int lightmapIndex = -1; // Renderer上使用的Lightmap纹理索引
        public Vector4 lightmapScaleOffset = new Vector4( 1f, 1f, 0f, 0f );
        public Bounds GetBounds() {
            return this.bounds;
        }
        public bool IsSubdividable() {
            return true;
        }
    }

    class LightmapRect {
        public Renderer renderer;
        public Vector4 meshUVBounds;
        public Vector4 lightmapUVBounds;
        public Vector4 lightmapPixelBounds;
    }

    struct MeshLightmapUVInfo {
        public int lightmapIndex; // Renderer上使用的Lightmap纹理索引
        public Vector4 srcUVBounds; // Mesh上用于Lightmap的纹理坐标包围盒
        public Vector4 lightmapUVBounds; // 通过Renderer的ScaleOffset变换后的包围盒
    }

    // 保存一个Lightmap纹理HDR全部像素信息
    class LightmapInfo {
        public int width = 0;
        public int height = 0;
        public Vector4[] pixels = null;
        public String assetPath = String.Empty;
    }

    struct AtlasPixelsPage {
        public int size;
        public Vector4[] pixels;
    }

    public static String currentScene {
        get {
            return UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path;
        }
    }

    static List<Transform> _SearchStack = new List<Transform>();
    static void WalkHierarchy( Transform current, Func<Transform, bool> walker ) {
        var oriSize = _SearchStack.Count;
        try {
            _SearchStack.Add( current );
            while ( _SearchStack.Count > oriSize ) {
                var last = _SearchStack.Count - 1;
                var cur = _SearchStack[ last ];
                _SearchStack.RemoveAt( last );
                if ( walker( cur ) == false ) {
                    continue;
                }
                for ( int i = 0; i < cur.childCount; ++i ) {
                    _SearchStack.Add( cur.GetChild( i ) );
                }
            }
        } finally {
            _SearchStack.Resize( oriSize );
        }
    }

    #region 用于收集选中节点下所有使用了Lightmap的MeshRenderer
    static Dictionary<Renderer, RendererInfo> CollectAllMeshRenderers( GameObject root, out Bounds bounds, Func<Renderer, Mesh, bool> filter = null ) {
        root = ( root ?? Selection.activeGameObject );
        Dictionary<Renderer, RendererInfo> allRendererSet = new Dictionary<Renderer, RendererInfo>();
        Vector3 _worldMin = new Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
        Vector3 _worldMax = new Vector3( float.MinValue, float.MinValue, float.MinValue );
        if ( root != null ) {
            WalkHierarchy(
                root.transform,
                t => {
                    if ( t.gameObject.activeSelf == false ) {
                        return false;
                    }
                    Renderer renderer = null;
                    Mesh mesh = null;
                    MeshRenderer component = t.GetComponent<MeshRenderer>();
                    if ( component != null ) {
                        MeshFilter component2 = t.GetComponent<MeshFilter>();
                        if ( component2 != null ) {
                            mesh = component2.sharedMesh;
                            renderer = component;
                        }
                    } else {
                        SkinnedMeshRenderer component3 = t.GetComponent<SkinnedMeshRenderer>();
                        if ( component3 != null ) {
                            mesh = component3.sharedMesh;
                            renderer = component3;
                        }
                    }
                    bool result;
                    if ( renderer != null && mesh != null ) {
                        if ( filter != null && !filter( renderer, mesh ) ) {
                            result = true;
                            return result;
                        }
                        RendererInfo rendererInfo = new RendererInfo();
                        rendererInfo.renderer = renderer;
                        rendererInfo.mesh = mesh;
                        rendererInfo.materials = renderer.sharedMaterials;
                        rendererInfo.lightmapIndex = renderer.lightmapIndex;
                        rendererInfo.lightmapScaleOffset = renderer.lightmapScaleOffset;
                        rendererInfo.bounds = renderer.bounds;
                        rendererInfo.bounds_radius = renderer.bounds.extents.magnitude;
                        Bounds bounds3 = rendererInfo.bounds;
                        Vector3 min = bounds3.min;
                        Vector3 max = bounds3.max;
                        if ( min.x < _worldMin.x ) {
                            _worldMin.x = min.x;
                        }
                        if ( min.y < _worldMin.y ) {
                            _worldMin.y = min.y;
                        }
                        if ( min.z < _worldMin.z ) {
                            _worldMin.z = min.z;
                        }
                        if ( max.x > _worldMax.x ) {
                            _worldMax.x = max.x;
                        }
                        if ( max.y > _worldMax.y ) {
                            _worldMax.y = max.y;
                        }
                        if ( max.z > _worldMax.z ) {
                            _worldMax.z = max.z;
                        }
                        allRendererSet.Add( renderer, rendererInfo );
                    }
                    result = true;
                    return result;
                }
            );
        }
        Bounds bounds2 = default( Bounds );
        bounds2.SetMinMax( _worldMin, _worldMax );
        bounds = bounds2;
        return allRendererSet;
    }

    static Dictionary<Renderer, RendererInfo> CollectAllMeshRenderers( GameObject[] roots, out Bounds bounds, Func<Renderer, Mesh, bool> filter = null ) {
        Bounds bounds_;
        var renderersInfo = CollectAllMeshRenderers( roots[ 0 ], out bounds_, filter );
        for ( int i = 1; i < roots.Length; ++i ) {
            Bounds _bounds;
            var _renderersInfo = CollectAllMeshRenderers( roots[ i ], out _bounds, filter );
            foreach ( var item in _renderersInfo ) {
                var r = item.Key;
                if ( !renderersInfo.ContainsKey( item.Key ) ) {
                    renderersInfo.Add( item.Key, item.Value );
                }
            }
            bounds_.Encapsulate( _bounds );
        }
        bounds = bounds_;
        return renderersInfo;
    }

    static Dictionary<Renderer, RendererInfo> CollectAllLightmapedRenderers( GameObject root, out Bounds bounds ) {
        root = ( root ?? Selection.activeGameObject );
        Dictionary<Renderer, RendererInfo> allRendererSet = new Dictionary<Renderer, RendererInfo>();
        Vector3 _worldMin = new Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
        Vector3 _worldMax = new Vector3( float.MinValue, float.MinValue, float.MinValue );
        if ( root != null ) {
            WalkHierarchy(
                root.transform,
                t => {
                    var mr = t.GetComponent<MeshRenderer>();
                    var mf = t.GetComponent<MeshFilter>();
                    if ( mf != null && mr != null && mr.lightmapIndex >= 0 && mf.sharedMesh != null ) {
                        RendererInfo rendererInfo = new RendererInfo();
                        rendererInfo.renderer = mr;
                        rendererInfo.lightmapIndex = mr.lightmapIndex;
                        rendererInfo.lightmapScaleOffset = mr.lightmapScaleOffset;
                        rendererInfo.bounds = mr.bounds;
                        rendererInfo.bounds_radius = mr.bounds.extents.magnitude;
                        rendererInfo.mesh = mf.sharedMesh;
                        Bounds bounds3 = rendererInfo.bounds;
                        Vector3 min = bounds3.min;
                        Vector3 max = bounds3.max;
                        if ( min.x < _worldMin.x ) {
                            _worldMin.x = min.x;
                        }
                        if ( min.y < _worldMin.y ) {
                            _worldMin.y = min.y;
                        }
                        if ( min.z < _worldMin.z ) {
                            _worldMin.z = min.z;
                        }
                        if ( max.x > _worldMax.x ) {
                            _worldMax.x = max.x;
                        }
                        if ( max.y > _worldMax.y ) {
                            _worldMax.y = max.y;
                        }
                        if ( max.z > _worldMax.z ) {
                            _worldMax.z = max.z;
                        }
                        allRendererSet.Add( mr, rendererInfo );
                    }
                    return true;
                }
            );
        }
        Bounds bounds2 = default( Bounds );
        bounds2.SetMinMax( _worldMin, _worldMax );
        bounds = bounds2;
        return allRendererSet;
    }

    static Dictionary<Renderer, RendererInfo> CollectAllLightmapedRenderers( GameObject[] roots, out Bounds bounds ) {
        Bounds bounds_;
        var renderersInfo = CollectAllLightmapedRenderers( roots[ 0 ], out bounds_ );
        for ( int i = 1; i < roots.Length; ++i ) {
            Bounds _bounds;
            var _renderersInfo = CollectAllMeshRenderers( roots[ i ], out _bounds );
            foreach ( var item in _renderersInfo ) {
                var r = item.Key;
                if ( !renderersInfo.ContainsKey( item.Key ) ) {
                    renderersInfo.Add( item.Key, item.Value );
                }
            }
            bounds_.Encapsulate( _bounds );
        }
        bounds = bounds_;
        return renderersInfo;
    }
    #endregion

    static void _CheckFolderPath( String path, ref bool hasCreateNew ) {
        if ( !Directory.Exists( path ) ) {
            FileUtils.CreateDirectory( path );
            hasCreateNew |= true;
        }
    }

    static String GetTempFolder() {
        var levelPath = currentScene;
        var innerPath = Path.GetDirectoryName( levelPath );
        var tempPath = String.Format( "{0}/{1}", innerPath, "temp" );
        var flag = false;
        _CheckFolderPath( tempPath, ref flag );
        if ( flag ) {
            AssetDatabase.Refresh();
        }
        return tempPath;
    }

    static String CleanTempFolder() {
        var path = GetTempFolder();
        if ( Directory.Exists( path ) ) {
            FileUtils.RemoveTree( path, false );
        }
        return path;
    }

    // 获取Mesh上的第UV2，如果没有，就回退返回第一套
    static Vector2[] GetMeshUV2( Mesh mesh, int channel = 1 ) {
        Vector2[] ret = null;
        var assetPath = AssetDatabase.GetAssetPath( mesh );
        Vector2[] result;
        if ( String.IsNullOrEmpty( assetPath ) ) {
            result = ret;
        } else {
            var all = AssetDatabase.LoadAllAssetRepresentationsAtPath( assetPath );
            int index = ( all.Length <= 0 ) ? ( ( !assetPath.EndsWith( ".asset" ) ) ? -1 : 0 ) : Array.IndexOf<UnityEngine.Object>( all, mesh );
            if ( !String.IsNullOrEmpty( assetPath ) && index >= 0 ) {
                ModelImporter ti = null;
                try {
                    ti = ( AssetImporter.GetAtPath( assetPath ) as ModelImporter );
                    if ( ti != null && !ti.isReadable ) {
                        ti.isReadable = true;
                        AssetDatabase.ImportAsset( assetPath );
                    }
                    var all_meshes = all.Where( o => o is Mesh ).ToList();
                    if ( ti == null && all_meshes.Count == 0 ) {
                        all_meshes.Add( mesh );
                    }
                    for ( int i = 0; i < all_meshes.Count; i++ ) {
                        int mindex = 0;
                        if ( all.Length > 0 ) {
                            mindex = Array.IndexOf<UnityEngine.Object>( all, all_meshes[ i ] );
                        }
                        Vector2[] uv = null;
                        Mesh mesh2 = all_meshes[ i ] as Mesh;
                        var _uv = new List<Vector2>();
                        mesh2.GetUVs( channel, _uv );
                        uv = _uv.ToArray();
                        if ( uv != null ) {
                            if ( mindex == index ) {
                                ret = uv;
                            }
                        }
                    }
                } finally {
                    if ( ti != null ) {
                        ti.isReadable = false;
                        AssetDatabase.ImportAsset( assetPath );
                    }
                }
            }
            result = ret;
        }
        return result;
    }

    // 获取Mesh上的UV2的包围盒，以及经过ScaleOffset变换后的包围盒，也就是在Lightmap纹理坐标系下的包围盒
    static MeshLightmapUVInfo GetMeshUV2Bounds( Renderer r ) {
        var ret = new MeshLightmapUVInfo();
        if ( r != null && r.lightmapIndex >= 0 ) {
            Mesh m = null;
            if ( r is MeshRenderer ) {
                var mf = r.GetComponent<MeshFilter>();
                if ( mf != null ) {
                    m = mf.sharedMesh;
                }
            } else if ( r is SkinnedMeshRenderer ) {
                var smr = r as SkinnedMeshRenderer;
                m = smr.sharedMesh;
            }
            if ( m != null ) {
                var channel = 1;
            RETRY:
                var uv = GetMeshUV2( m, channel );
                if ( uv != null ) {
                    var minx = float.MaxValue;
                    var miny = float.MaxValue;
                    var maxx = float.MinValue;
                    var maxy = float.MinValue;
                    var litmap_minx = float.MaxValue;
                    var litmap_miny = float.MaxValue;
                    var litmap_maxx = float.MinValue;
                    var litmap_maxy = float.MinValue;
                    var lightmapScaleOffset = r.lightmapScaleOffset;
                    for ( int _j = 0; _j < uv.Length; _j++ ) {
                        var _uv = uv[ _j ];
                        if ( _uv.x < minx ) {
                            minx = _uv.x;
                        }
                        if ( _uv.y < miny ) {
                            miny = _uv.y;
                        }
                        if ( _uv.x > maxx ) {
                            maxx = _uv.x;
                        }
                        if ( _uv.y > maxy ) {
                            maxy = _uv.y;
                        }
                        _uv.x *= lightmapScaleOffset.x;
                        _uv.y *= lightmapScaleOffset.y;
                        _uv.x += lightmapScaleOffset.z;
                        _uv.y += lightmapScaleOffset.w;
                        if ( _uv.x < litmap_minx ) {
                            litmap_minx = _uv.x;
                        }
                        if ( _uv.y < litmap_miny ) {
                            litmap_miny = _uv.y;
                        }
                        if ( _uv.x > litmap_maxx ) {
                            litmap_maxx = _uv.x;
                        }
                        if ( _uv.y > litmap_maxy ) {
                            litmap_maxy = _uv.y;
                        }
                    }
                    if ( uv.Length == 0 ) {
                        minx = 0;
                        miny = 0;
                        maxx = 0;
                        maxy = 0;
                        litmap_minx = 0;
                        litmap_miny = 0;
                        litmap_maxx = 0;
                        litmap_maxy = 0;
                    }
                    if ( channel > 0 ) {
                        // 防止取出来的包围盒是空的，继续往下获取一套能用的UV
                        if ( Mathf.Approximately( maxx, minx ) ||
                            Mathf.Approximately( maxy, miny ) ) {
                            --channel;
                            goto RETRY;
                        }
                    }
                    ret.lightmapIndex = r.lightmapIndex;
                    ret.srcUVBounds = new Vector4( minx, miny, maxx, maxy );
                    ret.lightmapUVBounds = new Vector4( litmap_minx, litmap_miny, litmap_maxx, litmap_maxy );
                }
            }
        }
        return ret;
    }

    // 把当前场景的lightmap像素以HDR格式读取出来保存成未压缩的浮点格式
    unsafe static List<LightmapInfo> LoadAllLightmapData() {
        var litmapData = new List<LightmapInfo>();
        var lightmaps = LightmapSettings.lightmaps;
        var count = 0;
        for ( int j = 0; j < lightmaps.Length; ++j ) {
            var out_data = IntPtr.Zero;
            var err = IntPtr.Zero;
            var width = 0;
            var height = 0;
            var assetPath = AssetDatabase.GetAssetPath( lightmaps[ j ].lightmapLight );
            var ld = new LightmapInfo();
            ld.assetPath = assetPath;
            try {
                if ( NativeAPI.LoadEXR( ref out_data, out width, out height, ld.assetPath, ref err ) >= 0 ) {
                    ld.pixels = new Vector4[ width * height ];
                    ld.width = width;
                    ld.height = height;
                    var src = ( Vector4* )out_data;
                    for ( int n = 0; n < ld.pixels.Length; ++n ) {
                        ld.pixels[ n ] = *src++;
                    }
                    ++count;
                } else {
                    if ( err != IntPtr.Zero ) {
                        var errorInfo = Marshal.PtrToStringAnsi( err );
                        UDebug.LogError( errorInfo );
                    }
                }
            } finally {
                if ( out_data != IntPtr.Zero ) {
                    NativeAPI.crt_free( out_data );
                }
            }
            litmapData.Add( ld );
        }
        return count == lightmaps.Length ? litmapData : null;
    }

    unsafe static void _RepackLightmap() {
        var gos = Selection.gameObjects;
        var checkMSG = false;
    REOPEN:
        if ( gos.Length == 0 ) {
            UDebug.LogError( "Please select a GameObject in hierarchy to process!" );
            return;
        }
        var hierarchy = UnityUtils.GetHierarchyPath( gos[ 0 ].transform );
        if ( !checkMSG ) {
            checkMSG = true;
            if ( !EditorUtility.DisplayDialog( Title, "请确认：处理的是原始场景，如果已经处理过，请重新加载场景。", "直接整", "好吧，重新加载场景" ) ) {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene( currentScene );
                gos = new GameObject[] { GameObject.Find( hierarchy ) };
                goto REOPEN;
            }
        }
        // 经过测试，这个值确实是输出到了最终lightmap纹理中纹理单位的间隔
        var Padding = LightmapEditorSettings.padding;
        Bounds bounds;
        var workingPath = CleanTempFolder();
        var allRenderers = CollectAllLightmapedRenderers( gos, out bounds ).Values.ToList();
        // 把搜集到的原始Mesh重新保存成asset格式，便于后面的修改（FBX无法修改）
        var meshes = new Dictionary<Mesh, Mesh>();
        for ( int i = 0; i < allRenderers.Count; ++i ) {
            var ri = allRenderers[ i ];
            var srcMesh = ri.mesh;
            if ( !srcMesh.isReadable ) {
                UDebug.LogError( "Mesh \"{0}\" is not readable!" );
                continue;
            }
            var mf = ri.renderer.transform.GetComponent<MeshFilter>();
            if ( mf == null ) {
                continue;
            }
            Mesh dstMesh = null;
            if ( !meshes.TryGetValue( srcMesh, out dstMesh ) ) {
                var assetPath = String.Format( "{0}/{1}_{2}.mesh.asset", workingPath, srcMesh.name, meshes.Count );
                if ( File.Exists( assetPath ) ) {
                    AssetDatabase.DeleteAsset( assetPath );
                }
                AssetDatabase.CreateAsset( UnityEngine.Object.Instantiate<Mesh>( srcMesh ), assetPath );
                dstMesh = AssetDatabase.LoadAssetAtPath<Mesh>( assetPath );
                meshes.Add( srcMesh, dstMesh );
            }
            //覆盖原始模型
            mf.sharedMesh = dstMesh;
        }
        if ( meshes.Count > 0 ) {
            var litmapData = LoadAllLightmapData();
            if ( litmapData == null ) {
                UDebug.LogError( "Load source lightmaps failed." );
                return;
            }
            var inputRects = new List<NativeAPI.stbrp_rect>();
            var all_atlas_pixels = new Dictionary<int, AtlasPixelsPage>();
            var lightmapRectInfoList = new List<LightmapRect>();
            using ( var tp = new TexturePacker() ) {
                for ( int i = 0; i < allRenderers.Count; ++i ) {
                    var ri = allRenderers[ i ];
                    var r = ri.renderer;
                    var uv2BoundsInfo = GetMeshUV2Bounds( r );
                    int lightmapIndex = uv2BoundsInfo.lightmapIndex;
                    if ( lightmapIndex >= 0 && lightmapIndex < litmapData.Count ) {
                        var lightmapData = litmapData[ r.lightmapIndex ];
                        var meshUVBounds = uv2BoundsInfo.srcUVBounds;
                        var lightmapUVBounds = uv2BoundsInfo.lightmapUVBounds;
                        var _lightmapUVBounds = lightmapUVBounds;

                        // 当前发现有些美术自己展开的UV有超过01现象，尝试自己模拟一下Repeat采样纹理坐标的计算
                        _lightmapUVBounds.x = MathLib.ClampRepeatUV( _lightmapUVBounds.x );
                        _lightmapUVBounds.y = MathLib.ClampRepeatUV( _lightmapUVBounds.y );
                        _lightmapUVBounds.z = MathLib.ClampRepeatUV( _lightmapUVBounds.z );
                        _lightmapUVBounds.w = MathLib.ClampRepeatUV( _lightmapUVBounds.w );

                        // 防止包围盒上下左右颠倒，重新翻转一下
                        if ( _lightmapUVBounds.x > _lightmapUVBounds.z ) {
                            var temp = _lightmapUVBounds.x;
                            _lightmapUVBounds.x = _lightmapUVBounds.z;
                            _lightmapUVBounds.z = temp;
                        }
                        if ( _lightmapUVBounds.y > _lightmapUVBounds.w ) {
                            var temp = _lightmapUVBounds.y;
                            _lightmapUVBounds.y = _lightmapUVBounds.w;
                            _lightmapUVBounds.w = temp;
                        }

                        // 纹理坐标到像素坐标转换，此处代码查看了网上流传出来unity4.3里面源码
                        var i_minx = Mathf.FloorToInt( _lightmapUVBounds.x * ( float )lightmapData.width );
                        var i_miny = Mathf.FloorToInt( _lightmapUVBounds.y * ( float )lightmapData.height );
                        var i_width = Mathf.CeilToInt( ( _lightmapUVBounds.z - _lightmapUVBounds.x ) * ( float )lightmapData.width );
                        var i_height = Mathf.CeilToInt( ( _lightmapUVBounds.w - _lightmapUVBounds.y ) * ( float )lightmapData.height );
                        
                        if ( i_width <= 0 || i_height <= 0 ) {
                            UDebug.LogError( "Invalid LightmapUV: {0}", UnityUtils.GetHierarchyPath( r.transform ) );
                            continue;
                        }
                        var i_maxx = i_minx + i_width;
                        var i_maxy = i_miny + i_height;
                        i_minx = Mathf.Clamp( i_minx, 0, lightmapData.width );
                        i_maxx = Mathf.Clamp( i_maxx, 0, lightmapData.width );
                        i_miny = Mathf.Clamp( i_miny, 0, lightmapData.height );
                        i_maxy = Mathf.Clamp( i_maxy, 0, lightmapData.height );

                        // 传入自己的TexturePacker之前，把border大小加上去
                        var rt = default( NativeAPI.stbrp_rect );
                        rt.w = ( ushort )( i_width + Padding );
                        rt.h = ( ushort )( i_height + Padding );

                        var lightmapRect = new LightmapRect();
                        lightmapRect.renderer = r;
                        lightmapRect.lightmapUVBounds = lightmapUVBounds;
                        lightmapRect.meshUVBounds = meshUVBounds;
                        lightmapRect.lightmapPixelBounds = new Vector4( i_minx, i_miny, i_maxx, i_maxy );
                        rt.id = lightmapRectInfoList.Count;
                        inputRects.Add( rt );
                        lightmapRectInfoList.Add( lightmapRect );
                    }
                }
                tp.PackRects( inputRects.ToArray() );
                tp.ForEach(
                    ( page, atlasSize, rt ) => {
                        var lightmapRect = lightmapRectInfoList[ rt.id ];
                        var renderer = lightmapRect.renderer;
                        var lightmapData = litmapData[ renderer.lightmapIndex ];
                        AtlasPixelsPage atlas_pixels;
                        if ( !all_atlas_pixels.TryGetValue( page, out atlas_pixels ) ) {
                            atlas_pixels = new AtlasPixelsPage {
                                pixels = new Vector4[ atlasSize * atlasSize ],
                                size = atlasSize
                            };
                            all_atlas_pixels.Add( page, atlas_pixels );
                        }
                        int i_minx = ( int )lightmapRect.lightmapPixelBounds.x;
                        int i_miny = ( int )lightmapRect.lightmapPixelBounds.y;
                        int i_maxx = ( int )lightmapRect.lightmapPixelBounds.z;
                        int i_maxy = ( int )lightmapRect.lightmapPixelBounds.w;

                        // lightmap像素块copy
                        fixed ( Vector4* _atlas_pixels = atlas_pixels.pixels ) {
                            for ( int y = i_miny; y < i_maxy; y++ ) {
                                int dy = y - i_miny + rt.y;
                                // 纹理坐标需要翻转一下，像素是翻转了的
                                int _dy = atlasSize - 1 - dy;
                                int _sy = lightmapData.height - 1 - y;
                                int _dy_stride = _dy * atlasSize;
                                int _sy_stride = _sy * lightmapData.width;
                                for ( int x = i_minx; x < i_maxx; x++ ) {
                                    int dx = x - i_minx + rt.x;
                                    _atlas_pixels[ _dy_stride + dx ] = lightmapData.pixels[ _sy_stride + x ];
                                }
                            }
                        }
                        // 计算在新的lightmap纹理下的纹理坐标包围盒，由于pack之前我们认为加了一个Padding，所以这里计算要减去
                        // 这里估计会有各种影藏的因素导致最终效果和原始效果出现偏差，黑边，偏移，防缩等
                        // 因为在复制像素的时候，包围盒因取整引入了误差，这里为了防止出现黑边，我人为的往里缩小了0.5
                        Vector4 uvBounds;
                        uvBounds.x = ( float )( rt.x + 0.5f ) / ( float )atlasSize;
                        uvBounds.y = ( float )( rt.y + 0.5f ) / ( float )atlasSize;
                        uvBounds.z = ( float )( ( rt.x + rt.w ) - Padding - 0.5f ) / ( float )atlasSize;
                        uvBounds.w = ( float )( ( rt.y + rt.h ) - Padding - 0.5f ) / ( float )atlasSize;
                        if ( Mathf.Approximately( lightmapRect.meshUVBounds.z, lightmapRect.meshUVBounds.x ) ||
                            Mathf.Approximately( lightmapRect.meshUVBounds.w, lightmapRect.meshUVBounds.y ) ) {
                            // 无效
                            renderer.lightmapIndex = -1;
                            renderer.lightmapScaleOffset = new Vector4( 1, 1, 0, 0 );
                            UDebug.LogError( "Invalid LightmapUV's bounds: {0}", UnityUtils.GetHierarchyPath( lightmapRect.renderer.transform ) );
                        } else {
                            // 计算新的ScaleOffset，映射到新的lightmap纹理
                            renderer.lightmapIndex = page;
                            renderer.lightmapScaleOffset = MathLib.CalculateUVScaleOffset( lightmapRect.meshUVBounds, uvBounds );
                        }
                    }
                );
                if ( all_atlas_pixels.Count > 0 ) {
                    var lightmapOutputPath = workingPath;
                    var lightmaps = new LightmapData[ all_atlas_pixels.Count ];
                    var lightmapPathList = new List<KeyValuePair<String, int>>( all_atlas_pixels.Count );
                    int pageIndex = 0;
                    foreach ( var pagePixels in all_atlas_pixels ) {
                        fixed ( Vector4* _atlas_pixels = pagePixels.Value.pixels ) {
                            EditorUtility.DisplayProgressBar( Title, "Saving lightmap atlas...", ( float )pageIndex / ( float )all_atlas_pixels.Count );
                            pageIndex++;
                            int atlasSize = pagePixels.Value.size;
                            var path = String.Format( "{0}/lightmap_{1}.exr", lightmapOutputPath, pagePixels.Key );
                            if ( File.Exists( path ) ) {
                                AssetDatabase.DeleteAsset( path );
                            }
                            NativeAPI.SaveEXR( ( IntPtr )_atlas_pixels, atlasSize, atlasSize, 4, 0, path );
                            lightmapPathList.Add( new KeyValuePair<String, int>( path, pagePixels.Key ) );
                        }
                    }
                    try {
                        AssetDatabase.Refresh();
                        pageIndex = 0;
                        for ( int j = 0; j < lightmapPathList.Count; j++ ) {
                            var path = lightmapPathList[ j ].Key;
                            var page = lightmapPathList[ j ].Value;
                            EditorUtility.DisplayProgressBar( Title, "Apply lightmap atlas...", ( float )pageIndex / ( float )all_atlas_pixels.Count );
                            pageIndex++;
                            var ti = AssetImporter.GetAtPath( path ) as TextureImporter;
                            if ( ti != null ) {
                                if ( ti.textureType != TextureImporterType.Lightmap || ti.isReadable || ti.wrapMode != TextureWrapMode.Clamp ) {
                                    ti.textureType = TextureImporterType.Lightmap;
                                    ti.isReadable = false;
                                    ti.wrapMode = TextureWrapMode.Clamp;
                                    AssetDatabase.ImportAsset( ti.assetPath );
                                }
                                var tex = AssetDatabase.LoadAssetAtPath( path, typeof( Texture2D ) ) as Texture2D;
                                if ( tex != null ) {
                                    lightmaps[ page ] = new LightmapData();
                                    lightmaps[ page ].lightmapLight = tex;
                                }
                            }
                        }
                    } finally {
                        LightmapSettings.lightmaps = lightmaps;
                    }
                }
            }
        }
    }

    [MenuItem( "Tools/LightmapRepacker/Repack For Selected GOs" )]
    static void RepackLightmap() {
        try {
            _RepackLightmap();
        } finally {
            EditorUtility.ClearProgressBar();
        }
    }

    [MenuItem( "Tools/LightmapRepacker/Reload Scene" )]
    static void Reload() {
        var go = Selection.activeGameObject;
        var hierarchy = String.Empty;
        if ( go != null ) {
            hierarchy = UnityUtils.GetHierarchyPath( go.transform );
        }
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene( currentScene );
        if ( !String.IsNullOrEmpty( hierarchy ) ) {
            go = GameObject.Find( hierarchy );
            Selection.activeGameObject = go;
        }
        UDebug.Log( "Scene {0} reloaded.", currentScene );
    }

}

