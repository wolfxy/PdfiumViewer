using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace TSTest
{
    [TestClass]
    public class UnitTest1
    {
        [TestMethod]
        public void TestMethod1()
        {
           
             string file = @"D:\Users\Administrator\Desktop\1_6课课本.pdf";
             //string file = @"D:\Users\Administrator\Desktop\2.pdf";
             string fname = Path.GetFileNameWithoutExtension(file);
             var document = PdfiumViewer.PdfDocument.Load(file);
             if (document != null)
             {
                 int pageCount = document.PageCount;
                 pageCount = 10;
                 for (var index = 9; index < pageCount; index++)
                 {
                     List<Image> images = document.GetPageImages(index);
                     if (images.Count > 0)
                     {
                         for(var i = 0; i < images.Count; i++)
                         {
                             Image image = images[i];
                             image.Save($"D:\\Users\\Administrator\\Desktop\\imgs\\{fname}_{index+1}_{i+1}.png", ImageFormat.Png);
                             image.Dispose();
                         }
                     }
                 }
             }
           

            //string file = @"D:\Users\Administrator\Desktop\2.pdf";
            //PdfiumViewer.PdfDocument document = null;
            //try
            //{
            //    document = PdfiumViewer.PdfDocument.Load(file, "1234563");
            //}
            //catch (Exception e)
            //{
            //    document = PdfiumViewer.PdfDocument.Load(file, "123456");
            //}
            //if (document != null)
            //{
            //    int pageCount = document.PageCount;
            //    Debug.WriteLine($"Page count is {pageCount}");
            //}
        }

        [TestMethod]
        public void TestMerge()
        {
            string file = @"D:\Users\Administrator\Desktop\1_6课课本.pdf";
            string src = @"D:\Users\Administrator\Desktop\测试文件.pdf";
            using (var document = PdfiumViewer.PdfDocument.Load(file))
            {
                using (var srcdocument = PdfiumViewer.PdfDocument.Load(src))
                {
                    document.Merge(srcdocument);
                    document.Save(@"D:\Users\Administrator\Desktop\1_6.pdf");
                }
            }
        }
    }
}
