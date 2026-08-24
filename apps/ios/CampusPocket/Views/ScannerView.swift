import SwiftUI
import VisionKit

/// The document scanner. Wraps VisionKit, which already does edge detection, perspective
/// correction and multi-page capture far better than anything hand-rolled would.
///
/// Pages are written into the outbox as a single PDF, because that is what actually gets printed
/// or handed in — a folder of JPEGs is not a worksheet.
struct ScannerView: UIViewControllerRepresentable {
    @Environment(Outbox.self) private var outbox
    @Environment(\.dismiss) private var dismiss

    func makeCoordinator() -> Coordinator {
        Coordinator(outbox: outbox) { dismiss() }
    }

    func makeUIViewController(context: Context) -> VNDocumentCameraViewController {
        let controller = VNDocumentCameraViewController()
        controller.delegate = context.coordinator
        return controller
    }

    func updateUIViewController(_ controller: VNDocumentCameraViewController, context: Context) {}

    @MainActor
    final class Coordinator: NSObject, VNDocumentCameraViewControllerDelegate {
        private let outbox: Outbox
        private let finish: () -> Void

        init(outbox: Outbox, finish: @escaping () -> Void) {
            self.outbox = outbox
            self.finish = finish
        }

        func documentCameraViewController(
            _ controller: VNDocumentCameraViewController,
            didFinishWith scan: VNDocumentCameraScan
        ) {
            defer { finish() }
            guard scan.pageCount > 0 else { return }

            let document = PDFBuilder.make(from: (0..<scan.pageCount).map { scan.imageOfPage(at: $0) })
            guard let data = document else { return }

            do {
                let stored = try outbox.writeAttachment(data, extension: "pdf")
                let title = scan.title.isEmpty ? "Scan" : scan.title
                outbox.add(CaptureItem(
                    kind: .photo,
                    title: "\(title) (\(scan.pageCount) page\(scan.pageCount == 1 ? "" : "s"))",
                    attachment: stored.name,
                    attachmentBytes: stored.bytes))
            } catch {
                // Failing to write is worth reporting, but never worth losing the scanner over.
                print("Campus: could not store scan — \(error.localizedDescription)")
            }
        }

        func documentCameraViewControllerDidCancel(_ controller: VNDocumentCameraViewController) {
            finish()
        }

        func documentCameraViewController(
            _ controller: VNDocumentCameraViewController,
            didFailWithError error: Error
        ) {
            finish()
        }
    }
}

/// Turns scanned pages into one PDF at their own size, so nothing is stretched to fit A4.
enum PDFBuilder {
    static func make(from images: [UIImage]) -> Data? {
        guard !images.isEmpty else { return nil }

        let data = NSMutableData()
        guard let consumer = CGDataConsumer(data: data as CFMutableData) else { return nil }

        var mediaBox = CGRect(origin: .zero, size: images[0].size)
        guard let context = CGContext(consumer: consumer, mediaBox: &mediaBox, nil) else { return nil }

        for image in images {
            guard let cgImage = image.cgImage else { continue }
            var page = CGRect(origin: .zero, size: image.size)
            context.beginPage(mediaBox: &page)
            context.draw(cgImage, in: page)
            context.endPage()
        }

        context.closePDF()
        return data as Data
    }
}
