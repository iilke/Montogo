import Foundation
import Metal
import MetalKit
import CoreVideo
import QuartzCore

// Metal-based YCbCr → RGB renderer. Drives an MTKView.
final class VideoRenderer: NSObject, MTKViewDelegate {
    private let device: MTLDevice
    private let commandQueue: MTLCommandQueue
    private let pipelineState: MTLRenderPipelineState
    private var textureY: MTLTexture?
    private var textureCbCr: MTLTexture?
    private var textureCache: CVMetalTextureCache?
    private var pendingTrace: FrameTrace?   // timing for the frame currently in textureY/CbCr
    private let lock = NSLock()

    init?(mtkView: MTKView) {
        guard let device = MTLCreateSystemDefaultDevice(),
              let queue = device.makeCommandQueue()
        else { return nil }

        self.device = device
        self.commandQueue = queue
        mtkView.device = device
        mtkView.framebufferOnly = true
        mtkView.colorPixelFormat = .bgra8Unorm

        // Build render pipeline from embedded shader source.
        guard let lib = try? device.makeLibrary(source: VideoRenderer.shaderSource, options: nil),
              let vert = lib.makeFunction(name: "vertexPassthrough"),
              let frag = lib.makeFunction(name: "fragmentYCbCr")
        else { return nil }

        let desc = MTLRenderPipelineDescriptor()
        desc.vertexFunction = vert
        desc.fragmentFunction = frag
        desc.colorAttachments[0].pixelFormat = mtkView.colorPixelFormat

        guard let pipe = try? device.makeRenderPipelineState(descriptor: desc) else { return nil }
        self.pipelineState = pipe

        CVMetalTextureCacheCreate(nil, nil, device, nil, &textureCache)

        super.init()
        mtkView.delegate = self
        mtkView.isPaused = true
        mtkView.enableSetNeedsDisplay = true
    }

    // Called from the decoder's callback; safe to call from any thread.
    func present(_ pixelBuffer: CVPixelBuffer, trace: FrameTrace? = nil) {
        lock.lock()
        defer { lock.unlock() }
        guard let cache = textureCache else { return }

        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)

        textureY   = makeTexture(cache: cache, buffer: pixelBuffer, plane: 0, format: .r8Unorm, width: width,     height: height)
        textureCbCr = makeTexture(cache: cache, buffer: pixelBuffer, plane: 1, format: .rg8Unorm, width: width/2, height: height/2)
        pendingTrace = trace
    }

    // Ask the MTKView to redraw after a new frame is set.
    func setNeedsDisplay(_ view: MTKView) {
        DispatchQueue.main.async { view.setNeedsDisplay(view.bounds) }
    }

    // MARK: - MTKViewDelegate

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}

    func draw(in view: MTKView) {
        lock.lock()
        let y = textureY
        let cbcr = textureCbCr
        // Consume the trace so a redraw with the same textures (e.g. on resize)
        // is not logged twice.
        let trace = pendingTrace
        pendingTrace = nil
        lock.unlock()

        guard let y, let cbcr,
              let drawable = view.currentDrawable,
              let passDesc = view.currentRenderPassDescriptor,
              let cmdBuf = commandQueue.makeCommandBuffer(),
              let encoder = cmdBuf.makeRenderCommandEncoder(descriptor: passDesc)
        else { return }

        encoder.setRenderPipelineState(pipelineState)
        encoder.setFragmentTexture(y,    index: 0)
        encoder.setFragmentTexture(cbcr, index: 1)
        // Full-screen triangle (no vertex buffer needed).
        encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
        encoder.endEncoding()

        // Report timing once the GPU has actually finished the frame — the truest
        // "on screen" moment for the render stage and total end-to-end latency.
        if let trace, trace.firstChunk > 0 {
            cmdBuf.addCompletedHandler { _ in
                let onScreen = CACurrentMediaTime()
                FrameTimingLog.shared.record(
                    reassembly: trace.complete  - trace.firstChunk,
                    queue:      trace.submitted - trace.complete,
                    decode:     trace.decoded   - trace.submitted,
                    render:     onScreen        - trace.decoded,
                    total:      onScreen        - trace.firstChunk,
                    bytes:      trace.bytes,
                    chunks:     trace.chunks)
            }
        }

        cmdBuf.present(drawable)
        cmdBuf.commit()
    }

    // MARK: - Helpers

    private func makeTexture(cache: CVMetalTextureCache,
                             buffer: CVPixelBuffer,
                             plane: Int,
                             format: MTLPixelFormat,
                             width: Int,
                             height: Int) -> MTLTexture? {
        var ref: CVMetalTexture?
        CVMetalTextureCacheCreateTextureFromImage(nil, cache, buffer, nil,
                                                 format, width, height, plane, &ref)
        return ref.flatMap { CVMetalTextureGetTexture($0) }
    }

    // MARK: - Inline Metal shaders

    private static let shaderSource = """
    #include <metal_stdlib>
    using namespace metal;

    struct VertexOut {
        float4 position [[position]];
        float2 texCoord;
    };

    // Full-screen triangle covering NDC [-1,1] × [-1,1].
    vertex VertexOut vertexPassthrough(uint vid [[vertex_id]]) {
        float2 pos[3] = {float2(-1, -1), float2(3, -1), float2(-1, 3)};
        float2 uv[3]  = {float2(0, 1),  float2(2, 1),  float2(0, -1)};
        VertexOut out;
        out.position = float4(pos[vid], 0, 1);
        out.texCoord = uv[vid];
        return out;
    }

    // BT.601 limited-range YCbCr → RGB.
    fragment float4 fragmentYCbCr(VertexOut in [[stage_in]],
                                  texture2d<float> texY    [[texture(0)]],
                                  texture2d<float> texCbCr [[texture(1)]]) {
        constexpr sampler s(filter::linear);
        float  y    = texY.sample(s, in.texCoord).r;
        float2 cbcr = texCbCr.sample(s, in.texCoord).rg;
        float cb = cbcr.x - 0.5;
        float cr = cbcr.y - 0.5;
        // Limited-range: Y in [16/255, 235/255], scale to [0,1].
        y = (y - 16.0/255.0) * (255.0/219.0);
        float r = y + 1.402 * cr;
        float g = y - 0.344136 * cb - 0.714136 * cr;
        float b = y + 1.772 * cb;
        return float4(clamp(float3(r, g, b), 0.0, 1.0), 1.0);
    }
    """
}
