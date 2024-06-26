﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using static PdfiumViewer.NativeMethods;

namespace PdfiumViewer
{
    internal class PdfFile : IDisposable
    {
        private static readonly Encoding FPDFEncoding = new UnicodeEncoding(false, false, false);

        private IntPtr _document;
        private IntPtr _form;
        private bool _disposed;
        private NativeMethods.FPDF_FORMFILLINFO _formCallbacks;
        private GCHandle _formCallbacksHandle;
        private readonly int _id;
        private Stream _stream;

        public PdfFile(Stream stream, string password)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            PdfLibrary.EnsureLoaded();

            _stream = stream;
            _id = StreamManager.Register(stream);

            try
            {
                var document = NativeMethods.FPDF_LoadCustomDocument(stream, password, _id);
                if (document == IntPtr.Zero)
                    throw new PdfException((PdfError)NativeMethods.FPDF_GetLastError());

                LoadDocument(document);
            }
            catch (Exception)
            {
                try
                {
                    if (stream != null)
                    {
                        StreamManager.Unregister(_id);
                        stream.Close();
                        stream = null;
                    }
                }
                catch (Exception)
                {

                }
                throw;
            }
        }

        public PdfBookmarkCollection Bookmarks { get; private set; }

        public bool RenderPDFPageToDC(int pageNumber, IntPtr dc, int dpiX, int dpiY, int boundsOriginX, int boundsOriginY, int boundsWidth, int boundsHeight, NativeMethods.FPDF flags)
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);

            using (var pageData = new PageData(_document, _form, pageNumber))
            {
                NativeMethods.FPDF_RenderPage(dc, pageData.Page, boundsOriginX, boundsOriginY, boundsWidth, boundsHeight, 0, flags);
            }

            return true;
        }

        public bool RenderPDFPageToBitmap(int pageNumber, IntPtr bitmapHandle, int dpiX, int dpiY, int boundsOriginX, int boundsOriginY, int boundsWidth, int boundsHeight, int rotate, NativeMethods.FPDF flags, bool renderFormFill)
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);

            using (var pageData = new PageData(_document, _form, pageNumber))
            {
                if (renderFormFill)
                    flags &= ~NativeMethods.FPDF.ANNOT;

                NativeMethods.FPDF_RenderPageBitmap(bitmapHandle, pageData.Page, boundsOriginX, boundsOriginY, boundsWidth, boundsHeight, rotate, flags);

                if (renderFormFill)
                    NativeMethods.FPDF_FFLDraw(_form, bitmapHandle, pageData.Page, boundsOriginX, boundsOriginY, boundsWidth, boundsHeight, rotate, flags);
            }

            return true;
        }

        public PdfPageLinks GetPageLinks(int pageNumber, Size pageSize)
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);

            var links = new List<PdfPageLink>();

            using (var pageData = new PageData(_document, _form, pageNumber))
            {
                int link = 0;
                IntPtr annotation;

                while (NativeMethods.FPDFLink_Enumerate(pageData.Page, ref link, out annotation))
                {
                    var destination = NativeMethods.FPDFLink_GetDest(_document, annotation);
                    int? target = null;
                    string uri = null;

                    if (destination != IntPtr.Zero)
                        target = (int)NativeMethods.FPDFDest_GetPageIndex(_document, destination);

                    var action = NativeMethods.FPDFLink_GetAction(annotation);
                    if (action != IntPtr.Zero)
                    {
                        const uint length = 1024;
                        var sb = new StringBuilder(1024);
                        NativeMethods.FPDFAction_GetURIPath(_document, action, sb, length);

                        uri = sb.ToString();
                    }

                    var rect = new NativeMethods.FS_RECTF();

                    if (NativeMethods.FPDFLink_GetAnnotRect(annotation, rect) && (target.HasValue || uri != null))
                    {
                        links.Add(new PdfPageLink(
                            new RectangleF(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top),
                            target,
                            uri
                        ));
                    }
                }
            }

            return new PdfPageLinks(links);
        }

        public List<SizeF> GetPDFDocInfo()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);

            int pageCount = NativeMethods.FPDF_GetPageCount(_document);
            var result = new List<SizeF>(pageCount);

            for (int i = 0; i < pageCount; i++)
            {
                result.Add(GetPDFDocInfo(i));
            }

            return result;
        }

        public SizeF GetPDFDocInfo(int pageNumber)
        {
            double height;
            double width;
            NativeMethods.FPDF_GetPageSizeByIndex(_document, pageNumber, out width, out height);

            return new SizeF((float)width, (float)height);
        }

        public void Save(Stream stream)
        {
            NativeMethods.FPDF_SaveAsCopy(_document, stream, NativeMethods.FPDF_SAVE_FLAGS.FPDF_NO_INCREMENTAL);
        }

        protected void LoadDocument(IntPtr document)
        {
            _document = document;

            NativeMethods.FPDF_GetDocPermissions(_document);

            _formCallbacks = new NativeMethods.FPDF_FORMFILLINFO();
            _formCallbacksHandle = GCHandle.Alloc(_formCallbacks, GCHandleType.Pinned);

            // Depending on whether XFA support is built into the PDFium library, the version
            // needs to be 1 or 2. We don't really care, so we just try one or the other.

            for (int i = 1; i <= 2; i++)
            {
                _formCallbacks.version = i;

                _form = NativeMethods.FPDFDOC_InitFormFillEnvironment(_document, _formCallbacks);
                if (_form != IntPtr.Zero)
                    break;
            }

            NativeMethods.FPDF_SetFormFieldHighlightColor(_form, 0, 0xFFE4DD);
            NativeMethods.FPDF_SetFormFieldHighlightAlpha(_form, 100);

            NativeMethods.FORM_DoDocumentJSAction(_form);
            NativeMethods.FORM_DoDocumentOpenAction(_form);

            Bookmarks = new PdfBookmarkCollection();

            LoadBookmarks(Bookmarks, NativeMethods.FPDF_BookmarkGetFirstChild(document, IntPtr.Zero));
        }

        private void LoadBookmarks(PdfBookmarkCollection bookmarks, IntPtr bookmark)
        {
            if (bookmark == IntPtr.Zero)
                return;

            bookmarks.Add(LoadBookmark(bookmark));
            while ((bookmark = NativeMethods.FPDF_BookmarkGetNextSibling(_document, bookmark)) != IntPtr.Zero)
                bookmarks.Add(LoadBookmark(bookmark));
        }

        private PdfBookmark LoadBookmark(IntPtr bookmark)
        {
            var result = new PdfBookmark
            {
                Title = GetBookmarkTitle(bookmark),
                PageIndex = (int)GetBookmarkPageIndex(bookmark)
            };

            //Action = NativeMethods.FPDF_BookmarkGetAction(_bookmark);
            //if (Action != IntPtr.Zero)
            //    ActionType = NativeMethods.FPDF_ActionGetType(Action);

            var child = NativeMethods.FPDF_BookmarkGetFirstChild(_document, bookmark);
            if (child != IntPtr.Zero)
                LoadBookmarks(result.Children, child);

            return result;
        }

        private string GetBookmarkTitle(IntPtr bookmark)
        {
            uint length = NativeMethods.FPDF_BookmarkGetTitle(bookmark, null, 0);
            byte[] buffer = new byte[length];
            NativeMethods.FPDF_BookmarkGetTitle(bookmark, buffer, length);

            string result = Encoding.Unicode.GetString(buffer);
            if (result.Length > 0 && result[result.Length - 1] == 0)
                result = result.Substring(0, result.Length - 1);

            return result;
        }

        private uint GetBookmarkPageIndex(IntPtr bookmark)
        {
            IntPtr dest = NativeMethods.FPDF_BookmarkGetDest(_document, bookmark);
            if (dest != IntPtr.Zero)
                return NativeMethods.FPDFDest_GetPageIndex(_document, dest);

            return 0;
        }

        public PdfMatches Search(string text, bool matchCase, bool wholeWord, int startPage, int endPage)
        {
            var matches = new List<PdfMatch>();

            if (String.IsNullOrEmpty(text))
                return new PdfMatches(startPage, endPage, matches);

            for (int page = startPage; page <= endPage; page++)
            {
                using (var pageData = new PageData(_document, _form, page))
                {
                    NativeMethods.FPDF_SEARCH_FLAGS flags = 0;
                    if (matchCase)
                        flags |= NativeMethods.FPDF_SEARCH_FLAGS.FPDF_MATCHCASE;
                    if (wholeWord)
                        flags |= NativeMethods.FPDF_SEARCH_FLAGS.FPDF_MATCHWHOLEWORD;

                    var handle = NativeMethods.FPDFText_FindStart(pageData.TextPage, FPDFEncoding.GetBytes(text), flags, 0);

                    try
                    {
                        while (NativeMethods.FPDFText_FindNext(handle))
                        {
                            int index = NativeMethods.FPDFText_GetSchResultIndex(handle);

                            int matchLength = NativeMethods.FPDFText_GetSchCount(handle);

                            var result = new byte[(matchLength + 1) * 2];
                            NativeMethods.FPDFText_GetText(pageData.TextPage, index, matchLength, result);
                            string match = FPDFEncoding.GetString(result, 0, matchLength * 2);

                            matches.Add(new PdfMatch(
                                match,
                                new PdfTextSpan(page, index, matchLength),
                                page
                            ));
                        }
                    }
                    finally
                    {
                        NativeMethods.FPDFText_FindClose(handle);
                    }
                }
            }

            return new PdfMatches(startPage, endPage, matches);
        }

        public IList<PdfRectangle> GetTextBounds(PdfTextSpan textSpan)
        {
            using (var pageData = new PageData(_document, _form, textSpan.Page))
            {
                return GetTextBounds(pageData.TextPage, textSpan.Page, textSpan.Offset, textSpan.Length);
            }
        }

        public Point PointFromPdf(int page, PointF point)
        {
            using (var pageData = new PageData(_document, _form, page))
            {
                NativeMethods.FPDF_PageToDevice(
                    pageData.Page,
                    0,
                    0,
                    (int)pageData.Width,
                    (int)pageData.Height,
                    0,
                    point.X,
                    point.Y,
                    out var deviceX,
                    out var deviceY
                );

                return new Point(deviceX, deviceY);
            }
        }

        public Rectangle RectangleFromPdf(int page, RectangleF rect)
        {
            using (var pageData = new PageData(_document, _form, page))
            {
                NativeMethods.FPDF_PageToDevice(
                    pageData.Page,
                    0,
                    0,
                    (int)pageData.Width,
                    (int)pageData.Height,
                    0,
                    rect.Left,
                    rect.Top,
                    out var deviceX1,
                    out var deviceY1
                );

                NativeMethods.FPDF_PageToDevice(
                    pageData.Page,
                    0,
                    0,
                    (int)pageData.Width,
                    (int)pageData.Height,
                    0,
                    rect.Right,
                    rect.Bottom,
                    out var deviceX2,
                    out var deviceY2
                );

                return new Rectangle(
                    deviceX1,
                    deviceY1,
                    deviceX2 - deviceX1,
                    deviceY2 - deviceY1
                );
            }
        }

        public PointF PointToPdf(int page, Point point)
        {
            using (var pageData = new PageData(_document, _form, page))
            {
                NativeMethods.FPDF_DeviceToPage(
                    pageData.Page,
                    0,
                    0,
                    (int)pageData.Width,
                    (int)pageData.Height,
                    0,
                    point.X,
                    point.Y,
                    out var deviceX,
                    out var deviceY
                );

                return new PointF((float)deviceX, (float)deviceY);
            }
        }

        public RectangleF RectangleToPdf(int page, Rectangle rect)
        {
            using (var pageData = new PageData(_document, _form, page))
            {
                NativeMethods.FPDF_DeviceToPage(
                    pageData.Page,
                    0,
                    0,
                    (int)pageData.Width,
                    (int)pageData.Height,
                    0,
                    rect.Left,
                    rect.Top,
                    out var deviceX1,
                    out var deviceY1
                );

                NativeMethods.FPDF_DeviceToPage(
                    pageData.Page,
                    0,
                    0,
                    (int)pageData.Width,
                    (int)pageData.Height,
                    0,
                    rect.Right,
                    rect.Bottom,
                    out var deviceX2,
                    out var deviceY2
                );

                return new RectangleF(
                    (float)deviceX1,
                    (float)deviceY1,
                    (float)(deviceX2 - deviceX1),
                    (float)(deviceY2 - deviceY1)
                );
            }
        }

        private IList<PdfRectangle> GetTextBounds(IntPtr textPage, int page, int index, int matchLength)
        {
            var result = new List<PdfRectangle>();
            RectangleF? lastBounds = null;

            for (int i = 0; i < matchLength; i++)
            {
                var bounds = GetBounds(textPage, index + i);

                if (bounds.Width == 0 || bounds.Height == 0)
                    continue;

                if (
                    lastBounds.HasValue &&
                    AreClose(lastBounds.Value.Right, bounds.Left) &&
                    AreClose(lastBounds.Value.Top, bounds.Top) &&
                    AreClose(lastBounds.Value.Bottom, bounds.Bottom)
                )
                {
                    float top = Math.Max(lastBounds.Value.Top, bounds.Top);
                    float bottom = Math.Min(lastBounds.Value.Bottom, bounds.Bottom);

                    lastBounds = new RectangleF(
                        lastBounds.Value.Left,
                        top,
                        bounds.Right - lastBounds.Value.Left,
                        bottom - top
                    );

                    result[result.Count - 1] = new PdfRectangle(page, lastBounds.Value);
                }
                else
                {
                    lastBounds = bounds;
                    result.Add(new PdfRectangle(page, bounds));
                }
            }

            return result;
        }

        private bool AreClose(float p1, float p2)
        {
            return Math.Abs(p1 - p2) < 4f;
        }

        private RectangleF GetBounds(IntPtr textPage, int index)
        {
            NativeMethods.FPDFText_GetCharBox(
                textPage,
                index,
                out var left,
                out var right,
                out var bottom,
                out var top
            );

            return new RectangleF(
                (float)left,
                (float)top,
                (float)(right - left),
                (float)(bottom - top)
            );
        }

        public int GetPageCountObject(int page)
        {
            using (var pageData = new PageData(_document, _form, page))
            {
                int count = NativeMethods.FPDFPage_Count_Object(pageData.Page);
                return count;
            }
        }

        public object GetPageObject(int page, int index)
        {
            using (var pageData = new PageData(_document, _form, page))
            {
                IntPtr objectPtr = NativeMethods.FPDFPage_GetObject(pageData.Page, index);
                return objectPtr;
            }
        }



        public List<Image> GetPdfImages(int page)
        {
            List<Image> images = new List<Image>();
            using (var pageData = new PageData(_document, _form, page))
            {
                int count = NativeMethods.FPDFPage_Count_Object(pageData.Page);
                if (count > 0)
                {
                    for (var index = 0; index < count; index++)
                    {
                        var obj = NativeMethods.FPDFPage_GetObject(pageData.Page, index);
                        PageObjectTypes type = NativeMethods.FPDFPageObject_GetType(obj);
                        if (PageObjectTypes.FPDF_PAGEOBJ_IMAGE == type)
                        {
                            try
                            {
                                long length = NativeMethods.FPDFImageObj_GetImageDataRaw(obj, null, 0);
                                byte[] buffer = new byte[length];
                                NativeMethods.FPDFImageObj_GetImageDataRaw(obj, buffer, length);
                                try
                                {
                                    MemoryStream ms = new MemoryStream(buffer);
                                    Image image = Image.FromStream(ms);
                                    images.Add(image);
                                }
                                catch (Exception)
                                {
                                    IntPtr bitmap = NativeMethods.FPDFImageObj_GetBitmap(obj);
                                    int height = NativeMethods.FPDFBitmap_GetHeight(bitmap);
                                    int width = NativeMethods.FPDFBitmap_GetWidth(bitmap);
                                    int stride = NativeMethods.FPDFBitmap_GetStride(bitmap);
                                    IntPtr pointer = NativeMethods.FPDFBitmap_GetBuffer(bitmap);
                                    BitmapFormats format = NativeMethods.FPDFBitmap_GetFormat(bitmap);
                                    FPDF_IMAGEOBJ_METADATA metadata = FPDFImageObj_GetImageMetadata(obj, pageData.Page);
                                    Bitmap bmp = null;
                                    switch (format)
                                    {
                                        case BitmapFormats.FPDFBitmap_BGR:
                                            bmp = new Bitmap(width, height, stride, PixelFormat.Format24bppRgb, pointer);
                                            break;
                                        case BitmapFormats.FPDFBitmap_BGRA:
                                            bmp = new Bitmap(width, height, stride, PixelFormat.Format32bppArgb, pointer);
                                            break;
                                        case BitmapFormats.FPDFBitmap_BGRx:
                                            bmp = new Bitmap(width, height, stride, PixelFormat.Format32bppArgb, pointer);
                                            break;
                                        case BitmapFormats.FPDFBitmap_Gray:
                                            bmp = new Bitmap(width, height, stride, PixelFormat.Format8bppIndexed, pointer);
                                            bmp.SetResolution(metadata.horizontal_dpi, metadata.vertical_dpi);

                                            //long dataLength = height * width;
                                            //byte[] dataBuffer = new byte[dataLength];
                                            //Marshal.Copy(pointer, dataBuffer, 0, (int)dataLength);
                                            //bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
                                            //bmp.SetResolution(metadata.horizontal_dpi, metadata.vertical_dpi);
                                            //BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                                            //Marshal.Copy(dataBuffer, 0, data.Scan0, (int)dataLength);
                                            //bmp.UnlockBits(data);

                                            break;
                                    }
                                    //FPDF_IMAGEOBJ_METADATA metadata = FPDFImageObj_GetImageMetadata(obj, pageData.Page);
                                    //long bufferSize2 = height * width * metadata.bits_per_pixel / 8;
                                    //long bufferSize2 = height * stride;
                                    //byte[] dataBuffer = new byte[bufferSize2];
                                    //Marshal.Copy(pointer, dataBuffer, 0, (int)bufferSize2);
                                    //var nbitmap = Convert_DATA_TO_BITMAP(dataBuffer, stride, width, height);
                                    //var nbitmap = new Bitmap(width, height, stride, PixelFormat.Format32bppArgb, pointer);
                                    if (bmp != null)
                                    {
                                        images.Add(bmp);
                                    }
                                    //nbitmap.Save($"D:\\Users\\Administrator\\Desktop\\imgs\\TMP_{index}.png", ImageFormat.Png);
                                    //long bufferSize = NativeMethods.FPDFImageObj_GetImageDataRaw(obj, null, 0);
                                    //byte[] buffer = new byte[bufferSize];
                                    ////long dataSize = NativeMethods.FPDFImageObj_GetImageDataRaw(obj, buffer, bufferSize);
                                    //long dataSize = NativeMethods.FPDFImageObj_GetImageDataRaw(obj, buffer, bufferSize);
                                    //Bitmap bmp = Convert_BGRA_TO_ARGB(buffer, width, height);
                                    //images.Add(bmp);
                                    //long  dataSize = bufferSize;
                                    //MemoryStream memoryStream = new MemoryStream(buffer, 0, (int)dataSize);
                                    //Image img = Bitmap.FromStream(memoryStream);
                                    //images.Add(img);
                                    //int filterCount = NativeMethods.FPDFImageObj_GetImageFilterCount(obj);
                                    //if (filterCount > 0)
                                    //{
                                    //    StringBuilder stringBuilder = new StringBuilder(256);
                                    //    long filterLen = NativeMethods.FPDFImageObj_GetImageFilter(obj, 0, stringBuilder, 256);
                                    //    string n = stringBuilder.ToString();
                                    //}
                                }
                            }
                            catch (Exception)
                            {

                            }
                        }
                    }
                }
            }
            return images;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="other">The file will be merge to.</param>
        /// <returns></returns>
        public bool Merge(PdfFile other)
        {
            return Merge(other, null);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="other">The file will be merge to.</param>
        /// <param name="range">A page range string, Such as "1,3,5-7". The first page is one. If |pagerange| is NULL, all pages from |src_doc| are imported.</param>
        /// <returns></returns>
        public bool Merge(PdfFile other, string range)
        {
            return Merge(_document, other._document, range);
        }

        /// <summary>
        /// </summary>
        /// <param name="destDoc">The destination document for the pages.</param>
        /// <param name="srcDoc">The document to be imported.</param>
        /// <returns></returns>
        public bool Merge(IntPtr destDoc, IntPtr srcDoc)
        {
            return Merge(destDoc, srcDoc, null);
        }

        /// <summary>
        /// </summary>
        /// <param name="destDoc">The destination document for the pages.</param>
        /// <param name="srcDoc">The document to be imported.</param>
        /// <param name="range"> A page range string, Such as "1,3,5-7". The first page is one. If |pagerange| is NULL, all pages from |src_doc| are imported.</param>
        /// <returns></returns>
        public bool Merge(IntPtr destDoc, IntPtr srcDoc, string range)
        {
            int nowDestDocPagesCount = NativeMethods.FPDF_GetPageCount(destDoc);
            return NativeMethods.FPDF_ImportPages(destDoc, srcDoc, range, nowDestDocPagesCount);
        }

        // BGR TO RGB
        private Bitmap Convert_BGRA_TO_ARGB(byte[] DATA, int width, int height)
        {
            Bitmap Bm = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            int index;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // BGRA TO ARGB
                    index = 4 * (x + (y * width));
                    Color c = Color.FromArgb(
                         DATA[index + 3],
                        DATA[index + 2],
                        DATA[index + 1],
                        DATA[index + 0]);
                    Bm.SetPixel(x, y, c);
                }
            }
            return Bm;
        }

        public string GetPdfText2(int page)
        {
            StringBuilder stringBuilder = new StringBuilder();
            using (var pageData = new PageData(_document, _form, page))
            {
                int count = NativeMethods.FPDFPage_Count_Object(pageData.Page);
                if (count > 0)
                {
                    for (var index = 0; index < count; index++)
                    {
                        var obj = NativeMethods.FPDFPage_GetObject(pageData.Page, index);
                        PageObjectTypes type = NativeMethods.FPDFPageObject_GetType(obj);
                        if (PageObjectTypes.FPDF_PAGEOBJ_TEXT == type)
                        {
                            string str = GetPdfTextObjectText(pageData, obj);
                            stringBuilder.Append(str);
                        }
                    }
                }
            }
            return stringBuilder.ToString();
        }

        public string GetPdfText(int page)
        {
            using (var pageData = new PageData(_document, _form, page))
            {
                int length = NativeMethods.FPDFText_CountChars(pageData.TextPage);
                return GetPdfText(pageData, new PdfTextSpan(page, 0, length + 1));
            }
        }

        public string GetPdfText(PdfTextSpan textSpan)
        {
            using (var pageData = new PageData(_document, _form, textSpan.Page))
            {
                return GetPdfText(pageData, textSpan);
            }
        }

        private string GetPdfText(PageData pageData, PdfTextSpan textSpan)
        {
            var result = new byte[(textSpan.Length + 1) * 2];
            NativeMethods.FPDFText_GetText(pageData.TextPage, textSpan.Offset, textSpan.Length, result);
            return FPDFEncoding.GetString(result, 0, textSpan.Length * 2);
        }

        private string GetPdfTextObjectText(PageData pageData, IntPtr textObject)
        {
            return NativeMethods.FPDFTextObject_GetText(pageData.Page, textObject);
        }

        public void DeletePage(int pageNumber)
        {
            NativeMethods.FPDFPage_Delete(_document, pageNumber);
        }

        public void RotatePage(int pageNumber, PdfRotation rotation)
        {
            using (var pageData = new PageData(_document, _form, pageNumber))
            {
                NativeMethods.FPDFPage_SetRotation(pageData.Page, rotation);
            }
        }

        public PdfInformation GetInformation()
        {
            var pdfInfo = new PdfInformation();

            pdfInfo.Creator = GetMetaText("Creator");
            pdfInfo.Title = GetMetaText("Title");
            pdfInfo.Author = GetMetaText("Author");
            pdfInfo.Subject = GetMetaText("Subject");
            pdfInfo.Keywords = GetMetaText("Keywords");
            pdfInfo.Producer = GetMetaText("Producer");
            pdfInfo.CreationDate = GetMetaTextAsDate("CreationDate");
            pdfInfo.ModificationDate = GetMetaTextAsDate("ModDate");

            return pdfInfo;
        }

        private string GetMetaText(string tag)
        {
            // Length includes a trailing \0.

            uint length = NativeMethods.FPDF_GetMetaText(_document, tag, null, 0);
            if (length <= 2)
                return string.Empty;

            byte[] buffer = new byte[length];
            NativeMethods.FPDF_GetMetaText(_document, tag, buffer, length);

            return Encoding.Unicode.GetString(buffer, 0, (int)(length - 2));
        }

        public DateTime? GetMetaTextAsDate(string tag)
        {
            string dt = GetMetaText(tag);

            if (string.IsNullOrEmpty(dt))
                return null;

            Regex dtRegex =
                new Regex(
                    @"(?:D:)(?<year>\d\d\d\d)(?<month>\d\d)(?<day>\d\d)(?<hour>\d\d)(?<minute>\d\d)(?<second>\d\d)(?<tz_offset>[+-zZ])?(?<tz_hour>\d\d)?'?(?<tz_minute>\d\d)?'?");

            Match match = dtRegex.Match(dt);

            if (match.Success)
            {
                var year = match.Groups["year"].Value;
                var month = match.Groups["month"].Value;
                var day = match.Groups["day"].Value;
                var hour = match.Groups["hour"].Value;
                var minute = match.Groups["minute"].Value;
                var second = match.Groups["second"].Value;
                var tzOffset = match.Groups["tz_offset"]?.Value;
                var tzHour = match.Groups["tz_hour"]?.Value;
                var tzMinute = match.Groups["tz_minute"]?.Value;

                string formattedDate = $"{year}-{month}-{day}T{hour}:{minute}:{second}.0000000";

                if (!string.IsNullOrEmpty(tzOffset))
                {
                    switch (tzOffset)
                    {
                        case "Z":
                        case "z":
                            formattedDate += "+0";
                            break;
                        case "+":
                        case "-":
                            formattedDate += $"{tzOffset}{tzHour}:{tzMinute}";
                            break;
                    }
                }

                try
                {
                    return DateTime.Parse(formattedDate);
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return null;
        }

        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                StreamManager.Unregister(_id);

                if (_form != IntPtr.Zero)
                {
                    NativeMethods.FORM_DoDocumentAAction(_form, NativeMethods.FPDFDOC_AACTION.WC);
                    NativeMethods.FPDFDOC_ExitFormFillEnvironment(_form);
                    _form = IntPtr.Zero;
                }

                if (_document != IntPtr.Zero)
                {
                    NativeMethods.FPDF_CloseDocument(_document);
                    _document = IntPtr.Zero;
                }

                if (_formCallbacksHandle.IsAllocated)
                    _formCallbacksHandle.Free();

                if (_stream != null)
                {
                    _stream.Dispose();
                    _stream = null;
                }

                _disposed = true;
            }
        }

        private class PageData : IDisposable
        {
            private readonly IntPtr _form;
            private bool _disposed;

            public IntPtr Page { get; private set; }

            public IntPtr TextPage { get; private set; }

            public double Width { get; private set; }

            public double Height { get; private set; }

            public PageData(IntPtr document, IntPtr form, int pageNumber)
            {
                _form = form;

                Page = NativeMethods.FPDF_LoadPage(document, pageNumber);
                TextPage = NativeMethods.FPDFText_LoadPage(Page);
                NativeMethods.FORM_OnAfterLoadPage(Page, form);
                NativeMethods.FORM_DoPageAAction(Page, form, NativeMethods.FPDFPAGE_AACTION.OPEN);

                Width = NativeMethods.FPDF_GetPageWidth(Page);
                Height = NativeMethods.FPDF_GetPageHeight(Page);
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    NativeMethods.FORM_DoPageAAction(Page, _form, NativeMethods.FPDFPAGE_AACTION.CLOSE);
                    NativeMethods.FORM_OnBeforeClosePage(Page, _form);
                    NativeMethods.FPDFText_ClosePage(TextPage);
                    NativeMethods.FPDF_ClosePage(Page);

                    _disposed = true;
                }
            }
        }
    }
}
