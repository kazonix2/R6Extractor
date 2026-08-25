using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using Prism.Extensions;
using Prism.Render.Shader;
using Prism.Resources;
using RainbowForge;
using RainbowForge.Model;

namespace Prism.Render
{
	public class ModelRenderer
	{
		private readonly Func<PrismSettings> _settingsProvider;
		private PrismSettings Settings => _settingsProvider.Invoke();

		private readonly IRenderContext _renderContext;
		private readonly VertexBuffer _vbo = new();
		private readonly List<SceneBatch> _sceneBatches = new();
		private readonly Dictionary<ulong, int> _sceneTextureIds = new();
		private readonly List<SceneLight> _dynamicLights = new();
		private BakedGiLighting _bakedGi = BakedGiLighting.Default;
		private const int MaxShaderLights = 64;
		private static readonly Matrix4 SceneCoordinateConversion =
			Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-90));

		private Camera _camera;

		private BoundingBox[] _modelBb = Array.Empty<BoundingBox>();
		private Vector2 _prevMousePoint = Vector2.Zero;
		private Vector2 _rotation = new(-35.264f, 45);

		private int _screenVao = -1;
		private ShaderProgram _shaderModel;
		private ShaderProgram _shaderScreen;

		private bool _hasTexture;
		private int _textureId;

		private int _reflectionTextureId;
		private int _uvTextureId;
		private int _radianceTextureId;

		private Vector2 _translation = new(0, 0);
		private Framebuffer _viewFbo;

		private int _zoom = 1;

		private CompiledMeshObject _compiledMeshObject;

		private sealed class SceneBatch
		{
			public VertexBuffer VertexBuffer { get; init; }
			public int TextureId { get; init; } = -1;
			public Matrix4[] Instances { get; init; } = [Matrix4.Identity];
		}

		public ModelRenderer(IRenderContext renderContext, Func<PrismSettings> settingsProvider)
		{
			_renderContext = renderContext;
			_settingsProvider = settingsProvider;

			_camera = new Camera
			{
				Position = new Vector3(10, 10, 10),
				Rotation = new Vector2(35.264f, -45)
			};
		}

		private void CreateScreenVao()
		{
			float[] quadVertices =
			{
				// positions   // texCoords
				-1.0f, 1.0f, 0.0f, 1.0f,
				-1.0f, -1.0f, 0.0f, 0.0f,
				1.0f, -1.0f, 1.0f, 0.0f,

				-1.0f, 1.0f, 0.0f, 1.0f,
				1.0f, -1.0f, 1.0f, 0.0f,
				1.0f, 1.0f, 1.0f, 1.0f
			};

			_screenVao = GL.GenVertexArray();
			var screenVbo = GL.GenBuffer();
			GL.BindVertexArray(_screenVao);
			GL.BindBuffer(BufferTarget.ArrayBuffer, screenVbo);
			GL.BufferData(BufferTarget.ArrayBuffer, quadVertices.Length * sizeof(float), quadVertices,
				BufferUsageHint.StaticDraw);
			GL.EnableVertexAttribArray(0);
			GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
			GL.BufferData(BufferTarget.ArrayBuffer, quadVertices.Length * sizeof(float), quadVertices,
				BufferUsageHint.StaticDraw);
			GL.EnableVertexAttribArray(1);
			GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.BindVertexArray(0);
		}

		private void DrawFullscreenQuad()
		{
			GL.Disable(EnableCap.DepthTest);
			GL.Enable(EnableCap.Texture2D);

			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(TextureTarget.Texture2DMultisample, _viewFbo.Texture);

			GL.BindVertexArray(_screenVao);
			GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

			GL.Disable(EnableCap.Texture2D);
			GL.Enable(EnableCap.DepthTest);
		}

		public void Render()
		{
			var width = _renderContext.Width;
			var height = _renderContext.Height;

			if (_screenVao == -1)
			{
				GL.Enable(EnableCap.Texture2D);

				CreateScreenVao();
				_viewFbo = new Framebuffer(8, _renderContext.DefaultFramebuffer);

				_shaderScreen = new ShaderProgram(ResourceHelper.ReadResource("screen.frag"), ResourceHelper.ReadResource("screen.vert"));
				_shaderScreen.Uniforms.SetValue("texScene", 0);
				_shaderScreen.Uniforms.SetValue("samplesScene", _viewFbo.Samples);

				_shaderModel = new ShaderProgram(ResourceHelper.ReadResource("model.frag"), ResourceHelper.ReadResource("model.vert"));
				_shaderModel.Uniforms.SetValue("texModel", 1);
				_shaderModel.Uniforms.SetValue("texReflection", 2);
				_shaderModel.Uniforms.SetValue("texRadiance", 3);
				_shaderModel.Uniforms.SetValue("texUv", 4);
				_shaderModel.Uniforms.SetValue("colorIn", new Vector3(1, 1, 1));
				_shaderModel.Uniforms.SetValue("lightPos", new Vector3(0.6f, -1, 0.8f));
				_shaderModel.Uniforms.SetValue("lightColor", new Vector3(1f, 1f, 1f));
				_shaderModel.Uniforms.SetValue("giAmbientColor", _bakedGi.AmbientColor);
				_shaderModel.Uniforms.SetValue("giAmbientIntensity", _bakedGi.AmbientIntensity);
				_shaderModel.Uniforms.SetValue("dynamicLightCount", 0);

				_textureId = GL.GenTexture();

				_reflectionTextureId = GL.GenTexture();
				using var reflectionBmp = new Bitmap(ResourceHelper.GetResource("reflectionvenicematcap.png"));
				reflectionBmp.LoadGlTexture(_reflectionTextureId, TextureTarget.Texture2D);

				_uvTextureId = GL.GenTexture();
				using var uvBmp = new Bitmap(ResourceHelper.GetResource("uv_debug.png"));
				uvBmp.LoadGlTexture(_uvTextureId, TextureTarget.Texture2D);

				_radianceTextureId = GL.GenTexture();
				using var radianceBmp = new Bitmap(ResourceHelper.GetResource("radiancevenicematcap.png"));
				radianceBmp.LoadGlTexture(_radianceTextureId, TextureTarget.Texture2D);

				GL.ActiveTexture(TextureUnit.Texture2);
				GL.BindTexture(TextureTarget.Texture2D, _reflectionTextureId);

				GL.ActiveTexture(TextureUnit.Texture3);
				GL.BindTexture(TextureTarget.Texture2D, _radianceTextureId);

				GL.ActiveTexture(TextureUnit.Texture4);
				GL.BindTexture(TextureTarget.Texture2D, _uvTextureId);
			}

			if (width != _viewFbo.Width || height != _viewFbo.Height)
			{
				_viewFbo.Init(width, height);
				_shaderScreen.Uniforms.SetValue("width", width);
				_shaderScreen.Uniforms.SetValue("height", height);
			}

			var aspectRatio = width / (float)height;
			var perspective = Matrix4.CreatePerspectiveFieldOfView(MathHelper.PiOver2, aspectRatio, 1f, 65536);

			GL.Viewport(0, 0, width, height);

			GL.ClearColor(Color.Black);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			var rotation = Matrix4.CreateRotationY(MathHelper.DegreesToRadians(-_rotation.Y))
			               * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-_rotation.X));

			var view = rotation
			           * Matrix4.CreateScale((float)Math.Pow(10, _zoom / 10f) * Vector3.One)
			           * Matrix4.CreateTranslation(_translation.X, _translation.Y, -10);

			var modelspace = SceneCoordinateConversion;

			var model = modelspace;

			_shaderModel.Uniforms.SetValue("m", model);
			_shaderModel.Uniforms.SetValue("v", view);
			_shaderModel.Uniforms.SetValue("p", perspective);

			GL.Enable(EnableCap.DepthTest);
			GL.Disable(EnableCap.Texture2D);

			_viewFbo.Use();
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			// 3D Viewport
			{
				GL.PushMatrix();

				GL.MatrixMode(MatrixMode.Projection);
				GL.LoadMatrix(ref perspective);
				GL.MatrixMode(MatrixMode.Modelview);
				GL.LoadMatrix(ref view);

				RenderOriginAxes();

				GL.PopMatrix();

				// GL.MultMatrix(ref model);
				//
				// RenderBoundingBox();

				GL.Color4(Color.White);

				GL.ActiveTexture(TextureUnit.Texture1);
				GL.BindTexture(TextureTarget.Texture2D, _hasTexture ? _textureId : 0);

				var settings = Settings;
				_shaderModel.Uniforms.SetValue("useReflections", settings.Use3DReflections ? 1 : 0);
				_shaderModel.Uniforms.SetValue("useCheckerboard", settings.Use3DCheckerboard ? 1 : 0);
				UploadLightingUniforms();

				_shaderModel.Use();
				if (_sceneBatches.Count == 0)
				{
					_shaderModel.Uniforms.SetValue("m", model);
					_shaderModel.Uniforms.SetValue("hasTexture", _hasTexture ? 1 : 0);
					_vbo.Render(PrimitiveType.Triangles);
				}
				else
				{
					foreach (var batch in _sceneBatches)
					{
						GL.ActiveTexture(TextureUnit.Texture1);
						GL.BindTexture(TextureTarget.Texture2D, batch.TextureId >= 0 ? batch.TextureId : 0);
						_shaderModel.SetActiveValue("hasTexture", batch.TextureId >= 0 ? 1 : 0);
						foreach (var instance in batch.Instances)
						{
							// Apply the Siege entity transform before converting the entire result
							// into viewport coordinates. The previous order rotated local geometry
							// but left translations in Siege space.
							_shaderModel.SetActiveValue("m", instance * modelspace);
							batch.VertexBuffer.Render(PrimitiveType.Triangles);
						}
					}
				}
				_shaderModel.Release();

				GL.Disable(EnableCap.Texture2D);
			}

			// 2D Viewport
			{
				GL.PushMatrix();

				GL.MatrixMode(MatrixMode.Projection);
				GL.LoadIdentity();
				GL.Ortho(0, width, height, 0, -100, 100);
				GL.MatrixMode(MatrixMode.Modelview);
				GL.LoadIdentity();

				// Gizmo
				GL.PushMatrix();
				GL.Translate(40, height - 40, 0);

				GL.Scale(30 * Vector3.One);
				GL.Scale(1, -1, 1);

				GL.MultMatrix(ref rotation);

				RenderOriginAxes();
				GL.PopMatrix();

				// UV map
				// GL.PushMatrix();
				// GL.Translate(width - 310, height - 310, 0);
				//
				// GL.Scale(300, 300, 1);
				//
				// GL.Color4(Color.Gray);
				// GL.Begin(PrimitiveType.Lines);
				//
				// GL.Vertex2(0, 0);
				// GL.Vertex2(1, 0);
				//
				// GL.Vertex2(1, 0);
				// GL.Vertex2(1, 1);
				//
				// GL.Vertex2(1, 1);
				// GL.Vertex2(0, 1);
				//
				// GL.Vertex2(0, 1);
				// GL.Vertex2(0, 0);
				//
				// GL.End();
				//
				// GL.Color4(Color.White);
				//
				// if (_mesh != null)
				// 	foreach (var o in _mesh.Objects.Take(1))
				// 	{
				// 		foreach (var pointer in o)
				// 		{
				// 			var a = _mesh.Container.TexCoords[pointer.A];
				// 			var b = _mesh.Container.TexCoords[pointer.B];
				// 			var c = _mesh.Container.TexCoords[pointer.C];
				//
				// 			GL.Begin(PrimitiveType.LineLoop);
				// 			GL.Vertex2(a.X, a.Y);
				// 			GL.Vertex2(b.X, b.Y);
				// 			GL.Vertex2(c.X, c.Y);
				// 			GL.End();
				// 		}
				// 	}
				//
				// GL.PopMatrix();

				GL.PopMatrix();
			}
			_viewFbo.Release();

			{
				GL.PushMatrix();

				GL.MatrixMode(MatrixMode.Projection);
				GL.LoadIdentity();
				GL.Ortho(-1, 1, -1, 1, -1, 1);
				GL.MatrixMode(MatrixMode.Modelview);
				GL.LoadIdentity();

				_shaderScreen.Use();
				DrawFullscreenQuad();
				_shaderScreen.Release();

				GL.PopMatrix();
			}
		}

		private static void RenderOriginAxes()
		{
			GL.Color4(Color.Red);
			GL.Begin(PrimitiveType.LineStrip);
			GL.Vertex3(0, 0, 0);
			GL.Vertex3(1, 0, 0);
			GL.End();
			GL.Color4(Color.LawnGreen);
			GL.Begin(PrimitiveType.LineStrip);
			GL.Vertex3(0, 0, 0);
			GL.Vertex3(0, 1, 0);
			GL.End();
			GL.Color4(Color.Blue);
			GL.Begin(PrimitiveType.LineStrip);
			GL.Vertex3(0, 0, 0);
			GL.Vertex3(0, 0, 1);
			GL.End();
		}

		private void RenderBoundingBox()
		{
			GL.Color4(Color.White);
			GL.Begin(PrimitiveType.Lines);

			foreach (var b in _modelBb)
			{
				var max = new Vector3(b.MaxX, b.MaxY, b.MaxZ);
				var min = new Vector3(b.MinX, b.MinY, b.MinZ);

				// X
				GL.Vertex3(min.X, min.Y, min.Z);
				GL.Vertex3(max.X, min.Y, min.Z);

				GL.Vertex3(min.X, max.Y, min.Z);
				GL.Vertex3(max.X, max.Y, min.Z);

				GL.Vertex3(min.X, max.Y, max.Z);
				GL.Vertex3(max.X, max.Y, max.Z);

				GL.Vertex3(min.X, min.Y, max.Z);
				GL.Vertex3(max.X, min.Y, max.Z);

				GL.Vertex3(min.X, min.Y, max.Z);
				GL.Vertex3(max.X, min.Y, max.Z);

				// Y
				GL.Vertex3(min.X, min.Y, min.Z);
				GL.Vertex3(min.X, max.Y, min.Z);

				GL.Vertex3(max.X, min.Y, min.Z);
				GL.Vertex3(max.X, max.Y, min.Z);

				GL.Vertex3(max.X, min.Y, max.Z);
				GL.Vertex3(max.X, max.Y, max.Z);

				GL.Vertex3(min.X, min.Y, max.Z);
				GL.Vertex3(min.X, max.Y, max.Z);

				// Z
				GL.Vertex3(min.X, min.Y, min.Z);
				GL.Vertex3(min.X, min.Y, max.Z);

				GL.Vertex3(min.X, max.Y, min.Z);
				GL.Vertex3(min.X, max.Y, max.Z);

				GL.Vertex3(max.X, max.Y, min.Z);
				GL.Vertex3(max.X, max.Y, max.Z);

				GL.Vertex3(max.X, min.Y, min.Z);
				GL.Vertex3(max.X, min.Y, max.Z);
			}

			GL.End();
		}

		public void OnMouseMove(Point devPt, bool leftMouse, bool rightMouse)
		{
			var mousePoint = new Vector2(devPt.X, devPt.Y);

			if (_prevMousePoint != Vector2.Zero)
			{
				if (rightMouse)
				{
					_translation.X += (mousePoint.X - _prevMousePoint.X) / 50f;
					_translation.Y -= (mousePoint.Y - _prevMousePoint.Y) / 50f;
				}
				else if (leftMouse)
				{
					_rotation.X -= (mousePoint.Y - _prevMousePoint.Y) / 2f;
					_rotation.Y -= (mousePoint.X - _prevMousePoint.X) / 2f;
				}
			}

			_prevMousePoint = mousePoint;

			_renderContext.MarkDirty();
		}

		public void OnMouseDown(Point devPt)
		{
			_prevMousePoint = new Vector2(devPt.X, devPt.Y);

			_renderContext.MarkDirty();
		}

		public void OnMouseWheel(float delta)
		{
			const int d = 1;
			var m1 = 1; //keyboard[Key.LShift] ? 10 : 1;
			var m2 = 1; //_keyboard[Key.LControl] ? 100 : 1;

			var deltaZoom = Math.Sign(delta) * d * m1 * m2;

			_zoom += deltaZoom;

			if (_zoom > 350)
				_zoom = 350;

			if (_zoom < -350)
				_zoom = -350;

			_renderContext.MarkDirty();
		}

		private static System.Numerics.Vector3[] ObjectColors =
		{
			new(0, 0, 1),
			new(0, 1, 0),
			new(0, 1, 1),
			new(1, 0, 0),
			new(1, 0, 1),
			new(1, 1, 0),
			new(1, 1, 1),
		};

		public void BuildModelQuads(CompiledMeshObject compiledMeshObject)
		{
			if (compiledMeshObject.Objects.Count == 0)
				return;

			ClearSceneBatches();

			_compiledMeshObject = compiledMeshObject;

			var colors = new System.Numerics.Vector3[compiledMeshObject.Container.Vertices.Length];
			var objectIds = new int[compiledMeshObject.Container.Vertices.Length];

			if (compiledMeshObject.Container.Colors != null)
			{
				for (var i = 0; i < compiledMeshObject.Container.Vertices.Length; i++)
				{
					var c = compiledMeshObject.Container.Colors[0, i];
					colors[i] = new System.Numerics.Vector3(c.R, c.G, c.B);
				}
			}

			for (var objectIdx = 0; objectIdx < compiledMeshObject.Objects.Count / compiledMeshObject.MeshHeader.NumLods; objectIdx++)
			{
				var trianglePointers = compiledMeshObject.Objects[objectIdx];
				foreach (var trianglePointer in trianglePointers)
				{
					objectIds[trianglePointer.A] = objectIdx;
					objectIds[trianglePointer.B] = objectIdx;
					objectIds[trianglePointer.C] = objectIdx;
				}
			}

			_vbo.InitializeVbo(
				compiledMeshObject.Container.Vertices,
				compiledMeshObject.Container.Normals,
				compiledMeshObject.Container.TexCoords,
				colors,
				objectIds,
				compiledMeshObject.Objects
					.Take((int)(compiledMeshObject.Objects.Count / compiledMeshObject.MeshHeader.NumLods))
					.SelectMany(pointers => pointers.SelectMany(pointer => new uint[] { pointer.A, pointer.B, pointer.C })).ToArray()
			);

			_renderContext.MarkDirty();
		}

		public void BeginTexturedScene()
		{
			ClearSceneBatches();
			_hasTexture = false;
			_dynamicLights.Clear();
			_bakedGi = BakedGiLighting.Default;
			_renderContext.MarkDirty();
		}

		public void BuildTexturedScene(IEnumerable<TexturedSceneMesh> sceneMeshes)
		{
			BeginTexturedScene();
			AppendTexturedScene(sceneMeshes);
		}

		public void AppendTexturedScene(IEnumerable<TexturedSceneMesh> sceneMeshes)
		{

			foreach (var sceneMesh in sceneMeshes)
			{
				var mesh = sceneMesh.Mesh;
				if (mesh?.Container?.Vertices == null || mesh.Objects.Count == 0 || mesh.MeshHeader.NumLods == 0)
					continue;

				var vertexCount = mesh.Container.Vertices.Length;
				var normals = mesh.Container.Normals ?? new System.Numerics.Vector3[vertexCount];
				var texCoords = mesh.Container.TexCoords ?? new System.Numerics.Vector2[vertexCount];
				var colors = new System.Numerics.Vector3[vertexCount];
				var objectIds = new int[vertexCount];
				var lod0ObjectCount = mesh.Objects.Count / (int)mesh.MeshHeader.NumLods;

				for (var objectIndex = 0; objectIndex < lod0ObjectCount; objectIndex++)
				{
					var elements = mesh.Objects[objectIndex]
						.SelectMany(pointer => new uint[] { pointer.A, pointer.B, pointer.C })
						.ToArray();
					if (elements.Length == 0)
						continue;

					foreach (var element in elements)
						if (element < objectIds.Length)
							objectIds[element] = objectIndex;

					var vbo = new VertexBuffer();
					vbo.InitializeVbo(mesh.Container.Vertices, normals, texCoords, colors, objectIds, elements);
					var textureId = -1;
					if (objectIndex < sceneMesh.ObjectTextures.Length && sceneMesh.ObjectTextures[objectIndex] != null)
					{
						var texture = sceneMesh.ObjectTextures[objectIndex];
						if (!_sceneTextureIds.TryGetValue(texture.Uid, out textureId))
						{
							textureId = GL.GenTexture();
							LoadSceneTexture(texture, textureId);
							_sceneTextureIds[texture.Uid] = textureId;
						}
					}

					_sceneBatches.Add(new SceneBatch
					{
						VertexBuffer = vbo,
						TextureId = textureId,
						Instances = sceneMesh.Instances
					});
				}
			}

			_renderContext.MarkDirty();
		}

		private static void LoadSceneTexture(SceneTextureData texture, int textureId)
		{
			if (texture.BgraPixels.Length != checked(texture.Width * texture.Height * 4))
				throw new InvalidDataException("Scene texture pixel buffer has an invalid length.");

			GL.BindTexture(TextureTarget.Texture2D, textureId);
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
			var handle = GCHandle.Alloc(texture.BgraPixels, GCHandleType.Pinned);
			try
			{
				GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
					texture.Width, texture.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Bgra,
					PixelType.UnsignedByte, handle.AddrOfPinnedObject());
			}
			finally
			{
				handle.Free();
			}
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
		}

		public void AppendDynamicLights(IEnumerable<SceneLight> lights)
		{
			if (lights != null)
				_dynamicLights.AddRange(lights.Take(8192 - Math.Min(8192, _dynamicLights.Count)));
			_renderContext.MarkDirty();
		}

		public void SetBakedLighting(BakedGiLighting lighting)
		{
			_bakedGi = lighting ?? BakedGiLighting.Default;
			_renderContext.MarkDirty();
		}

		private void UploadLightingUniforms()
		{
			_shaderModel.Uniforms.SetValue("giAmbientColor", _bakedGi.AmbientColor);
			_shaderModel.Uniforms.SetValue("giAmbientIntensity", _bakedGi.AmbientIntensity);
			var lightCount = Math.Min(MaxShaderLights, _dynamicLights.Count);
			_shaderModel.Uniforms.SetValue("dynamicLightCount", lightCount);
			for (var index = 0; index < lightCount; index++)
			{
				// Evenly sample a large map's light set instead of retaining only the
				// first loaded sector.
				var sourceIndex = lightCount == _dynamicLights.Count
					? index
					: (int)((long)index * _dynamicLights.Count / lightCount);
				var light = _dynamicLights[sourceIndex];
				var position = Vector3.TransformPosition(light.Position, SceneCoordinateConversion);
				var direction = Vector3.Normalize(Vector3.TransformVector(light.Direction, SceneCoordinateConversion));
				_shaderModel.Uniforms.SetValue($"dynamicLightPosition[{index}]", position);
				_shaderModel.Uniforms.SetValue($"dynamicLightDirection[{index}]", direction);
				_shaderModel.Uniforms.SetValue($"dynamicLightColor[{index}]", light.Color);
				_shaderModel.Uniforms.SetValue($"dynamicLightIntensity[{index}]", light.Intensity);
				_shaderModel.Uniforms.SetValue($"dynamicLightRadius[{index}]", light.Radius);
				_shaderModel.Uniforms.SetValue($"dynamicLightConeCosine[{index}]", light.OuterConeCosine);
				_shaderModel.Uniforms.SetValue($"dynamicLightKind[{index}]", light.Kind == SceneLightKind.Spot ? 1 : 0);
			}
		}

		private void ClearSceneBatches()
		{
			foreach (var batch in _sceneBatches)
			{
				batch.VertexBuffer.Dispose();
			}
			foreach (var textureId in _sceneBatches.Select(batch => batch.TextureId).Where(id => id >= 0).Distinct())
				GL.DeleteTexture(textureId);
			_sceneBatches.Clear();
			_sceneTextureIds.Clear();
		}

		public void SetPartBounds(BoundingBox[] bounds)
		{
			_modelBb = bounds;

			_renderContext.MarkDirty();
		}

		public void SetTexture(Bitmap bmp)
		{
			_hasTexture = bmp != null;

			if (bmp == null)
				return;

			bmp.LoadGlTexture(_textureId, TextureTarget.Texture2D);

			_renderContext.MarkDirty();
		}

		public void ResetView()
		{
			_zoom = 1;
			_rotation = new Vector2(-35.264f, 45);
			_translation = Vector2.Zero;

			_renderContext.MarkDirty();
		}
	}
}
