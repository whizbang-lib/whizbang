namespace Exp;

public static class Calc {
  public static string Sign(int x) {
    if (x > 0) {
      return "pos";
    }
    return "nonpos";
  }

  public static int Pick(int x) {
    if (x > 0) {
      return 1;
    }
    if (x < -5) {
      return 2;
    }
    return 3;
  }
}
